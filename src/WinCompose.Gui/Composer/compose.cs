﻿//
// WinCompose — a compose key for Windows
//
// Copyright: (c) 2013-2014 Sam Hocevar <sam@hocevar.net>
//                     2014 Benjamin Litzelmann
//   This program is free software. It comes without any warranty, to
//   the extent permitted by applicable law. You can redistribute it
//   and/or modify it under the terms of the Do What the Fuck You Want
//   to Public License, Version 2, as published by the WTFPL Task Force.
//   See http://www.wtfpl.net/ for more details.

using System;
using System.Collections.Generic;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WinCompose.Gui
{

static class Compose
{
    // Get input from the keyboard hook; return true if the key was handled
    // and needs to be removed from the input chain.
    public static bool OnKey(WM ev, VK vk, SC sc, LLKHF flags)
    {
        CheckKeyboardLayout();

        bool is_keydown = (ev == WM.KEYDOWN || ev == WM.SYSKEYDOWN);
        bool is_keyup = !is_keydown;

        bool has_shift = (GetKeyState(VK.SHIFT) & 0x80) == 0x80;
        bool has_altgr = (GetKeyState(VK.RMENU) & 0x80) == 0x80
                          && (GetKeyState(VK.LCONTROL) & 0x80) == 0x80;
        bool has_capslock = GetKeyState(VK.CAPITAL) != 0;

        // If we can not find a printable representation for the key, use its
        // virtual key code instead.
        Key key = new Key(vk);

        // Generate a keystate suitable for ToUnicode()
        GetKeyboardState(m_keystate);
        m_keystate[0x10] = (byte)(has_shift ? 0x80 : 0x00);
        m_keystate[0x11] = (byte)(has_altgr ? 0x80 : 0x00);
        m_keystate[0x12] = (byte)(has_altgr ? 0x80 : 0x00);
        m_keystate[0x14] = (byte)(has_capslock ? 0x01 : 0x00);
        int buflen = 4;
        byte[] buf = new byte[2 * buflen];
        int ret = ToUnicode(vk, sc, m_keystate, buf, buflen, flags);
        if (ret > 0 && ret < buflen)
        {
            // FIXME: if using dead keys, we may receive two keys from GetString!
            buf[ret * 2] = buf[ret * 2 + 1] = 0x00; // Null-terminate the string
            key = new Key(Encoding.Unicode.GetString(buf, 0, ret + 1));
        }

        // FIXME: we don’t properly support compose keys that also normally
        // print stuff, such as `.
        if (Settings.IsComposeKey(key))
        {
            if (is_keyup)
            {
                m_compose_down = false;
            }
            else if (is_keydown && !m_compose_down)
            {
                m_compose_down = true;
                m_composing = !m_composing;
                if (!m_composing)
                    m_sequence = new List<Key>();
            }
            return true;
        }

        // If we are not currently composing a sequence, do nothing
        if (!m_composing)
        {
            return false;
        }

        if (!Settings.IsUsableKey(key))
        {
            // FIXME: if compose key is down, maybe there is a key combination
            // going on, such as Alt+Tab or Windows+Up
            return false;
        }

        // Finally, this is a key we must add to our compose sequence and
        // decide whether we'll eventually output a character.
        if (is_keydown)
        {
            m_sequence.Add(key);

            // FIXME: we don’t support case-insensitive yet
            if (Settings.IsValidSequence(m_sequence))
            {
                // Sequence finished, print it
                SendString(Settings.GetSequenceResult(m_sequence));
                m_composing = false;
                m_sequence = new List<Key>();
            }
            else if (Settings.IsValidPrefix(m_sequence))
            {
                // Still a valid prefix, continue building sequence
            }
            else
            {
                // Unknown characters for sequence, print them if necessary
                if (!Settings.DiscardOnInvalid.Value)
                {
                    foreach (Key k in m_sequence)
                    {
                        // FIXME: what if the key is e.g. left arrow?
                        if (k.IsPrintable())
                            SendString(k.ToString());
                    }
                }

                if (Settings.BeepOnInvalid.Value)
                    SystemSounds.Beep.Play();

                m_composing = false;
                m_sequence = new List<Key>();
            }
        }

        return true;
    }

    private static void SendString(string str)
    {
        /* HACK: GTK+ applications behave differently with Unicode, and some
         * applications such as XChat for Windows rename their own top-level
         * window, so we parse through the names we know in order to detect
         * a GTK+ application. */
        bool is_gtk = false;
        const int len = 256;
        StringBuilder buf = new StringBuilder(len);
        if (GetClassName(GetForegroundWindow(), buf, len) > 0)
        {
            string wclass = buf.ToString();
            if (wclass == "gdkWindowToplevel" || wclass == "xchatWindowToplevel")
                is_gtk = true;
        }

        if (is_gtk)
        {
            /* Wikipedia says Ctrl+Shift+u, release, then type the four hex digits, and
             * press Enter (http://en.wikipedia.org/wiki/Unicode_input). */
            SendKeyDown(VK.LCONTROL);
            SendKeyDown(VK.LSHIFT);
            SendKeyPress((VK)'U');
            SendKeyUp(VK.LSHIFT);
            SendKeyUp(VK.LCONTROL);

            foreach (var ch in str)
                foreach (var key in String.Format("{0:X04} ", (short)ch))
                    SendKeyPress((VK)key);
        }
        else
        {
            INPUT[] input = new INPUT[str.Length];

            for (int i = 0; i < input.Length; i++)
            {
                input[i].type = EINPUT.KEYBOARD;
                input[i].U.ki.wVk = 0;
                input[i].U.ki.wScan = (ScanCodeShort)str[i];
                input[i].U.ki.time = 0;
                input[i].U.ki.dwFlags = KEYEVENTF.UNICODE;
                input[i].U.ki.dwExtraInfo = UIntPtr.Zero;
            }

            SendInput((uint)input.Length, input, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    public static bool IsComposing()
    {
        return m_composing;
    }

    private static void SendKeyDown(VK vk)
    {
        keybd_event(vk, 0, 0, 0);
    }

    private static void SendKeyUp(VK vk)
    {
        keybd_event(vk, 0, KEYEVENTF.KEYUP, 0);
    }

    private static void SendKeyPress(VK vk)
    {
        SendKeyDown(vk);
        SendKeyUp(vk);
    }

    // FIXME: this is useless for now
    private static void CheckKeyboardLayout()
    {
        // FIXME: the foreground window doesn't seem to notice keyboard
        // layout changes caused by the Win+Space combination.
        IntPtr hwnd = GetForegroundWindow();
        uint pid, tid = GetWindowThreadProcessId(hwnd, out pid);
        IntPtr active_layout = GetKeyboardLayout(tid);
        //Console.WriteLine("Active layout is {0:X}", (int)active_layout);

        tid = GetCurrentThreadId();
        IntPtr my_layout = GetKeyboardLayout(tid);
        //Console.WriteLine("WinCompose layout is {0:X}", (int)my_layout);
    }

    private static byte[] m_keystate = new byte[256];
    private static List<Key> m_sequence = new List<Key>();
    private static bool m_compose_down = false;
    private static bool m_composing = false;

    internal enum EINPUT : uint
    {
        MOUSE    = 0,
        KEYBOARD = 1,
        HARDWARE = 2,
    }

    [Flags]
    internal enum KEYEVENTF : uint
    {
        EXTENDEDKEY = 0x0001,
        KEYUP       = 0x0002,
        UNICODE     = 0x0004,
        SCANCODE    = 0x0008,
    }

    [Flags]
    internal enum MOUSEEVENTF : uint
    {
        // Not needed
    }

    internal enum VirtualKeyShort : short
    {
    }

    internal enum ScanCodeShort : short
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        internal EINPUT type;
        internal UINPUT U;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct UINPUT
    {
        // All union members need to be included, because they contribute
        // to the final size of struct INPUT.
        [FieldOffset(0)]
        internal MOUSEINPUT mi;
        [FieldOffset(0)]
        internal KEYBDINPUT ki;
        [FieldOffset(0)]
        internal HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        internal int dx, dy, mouseData;
        internal MOUSEEVENTF dwFlags;
        internal uint time;
        internal UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        internal VirtualKeyShort wVk;
        internal ScanCodeShort wScan;
        internal KEYEVENTF dwFlags;
        internal int time;
        internal UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        internal int uMsg;
        internal short wParamL, wParamH;
    }

    [DllImport("user32", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint SendInput(uint nInputs,
        [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);
    [DllImport("user32", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern void keybd_event(VK vk, SC sc, KEYEVENTF flags,
                                           int dwExtraInfo);

    [DllImport("user32", CharSet = CharSet.Auto)]
    private static extern int ToUnicode(VK wVirtKey, SC wScanCode,
                                        byte[] lpKeyState, byte[] pwszBuff,
                                        int cchBuff, LLKHF flags);
    [DllImport("user32", CharSet = CharSet.Auto)]
    private static extern int GetKeyboardState(byte[] lpKeyState);
    [DllImport("user32", CharSet = CharSet.Auto)]
    private static extern short GetKeyState(VK nVirtKey);

    [DllImport("user32", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("kernel32")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);
    [DllImport("imm32", CharSet = CharSet.Auto)]
    private static extern IntPtr ImmGetDefaultIMEWnd(HandleRef hwnd);
}

}