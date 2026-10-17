using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MediaTidy;

/// <summary>Gives each file its tidy name. On the same drive this is a hard link: a second name for
/// the same data, so no space is used and qBittorrent can keep seeding the original.</summary>
internal static partial class Linker
{
    public static void Apply(Plan plan)
    {
        foreach (var link in plan.Links)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link.To)!);
            if (SameDrive(link.From, link.To))
            {
                if (!CreateHardLink(link.To, link.From, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Couldn't link {link.To}");
                }
            }
            else
            {
                File.Copy(link.From, link.To);
            }
        }
    }

    private static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newFileName, string existingFileName, nint securityAttributes);
}
