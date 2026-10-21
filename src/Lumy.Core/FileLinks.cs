using System.Runtime.InteropServices;

namespace Lumy.Core;

/// <summary>
/// Puts a photo into an export folder without duplicating it: an NTFS hard link is a second name for the same
/// file, takes no extra space, and Instagram or a file manager sees an ordinary file. Across drives, or where
/// links are not supported, it falls back to a copy.
/// </summary>
public static class FileLinks
{
    /// <summary>Returns true for a link, false for a copy.</summary>
    public static bool LinkOrCopy(string source, string target)
    {
        if (OperatingSystem.IsWindows() && CreateHardLink(target, source, IntPtr.Zero))
            return true;

        File.Copy(source, target, overwrite: false);
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
