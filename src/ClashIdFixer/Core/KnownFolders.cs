using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClashIdFixer.Core
{
    /// <summary>
    /// Environment.SpecialFolder has no entry for Downloads, so it is asked from
    /// the shell directly (with a plain %USERPROFILE%\Downloads fallback).
    /// </summary>
    public static class KnownFolders
    {
        private static readonly Guid DownloadsFolderId = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        public static string Downloads
        {
            get
            {
                try
                {
                    var id = DownloadsFolderId;
                    IntPtr pathPtr;
                    if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out pathPtr) == 0)
                    {
                        try
                        {
                            string path = Marshal.PtrToStringUni(pathPtr);
                            if (!string.IsNullOrEmpty(path)) return path;
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(pathPtr);
                        }
                    }
                }
                catch
                {
                }

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }
    }
}
