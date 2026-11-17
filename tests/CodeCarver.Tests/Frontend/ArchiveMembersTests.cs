using System.Text;
using CodeCarver.Core.Frontend;
using Xunit;

namespace CodeCarver.Tests.Frontend;

public class ArchiveMembersTests
{
    static string Header(string name, int size) => $"{name,-16}{"0",-12}{"0",-6}{"0",-6}{"644",-8}{size,-10}`\n";

    static byte[] Gnu()
    {
        var sb = new StringBuilder("!<arch>\n");
        sb.Append(Header("/", 4)).Append("\0\0\0\0");
        var longNames = "kq_a_very_long_member_name.o/\n";
        sb.Append(Header("//", longNames.Length)).Append(longNames);
        sb.Append(Header("kq_short.o/", 3)).Append("abc\n");   // odd size: padded to even
        sb.Append(Header("/0", 2)).Append("xy");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void ReadsShortAndLongNames_SkippingTheTables()
    {
        var p = Path.Combine(Path.GetTempPath(), $"kq-ar-{Guid.NewGuid():N}.a");
        try
        {
            File.WriteAllBytes(p, Gnu());
            Assert.Equal(new[] { "kq_short.o", "kq_a_very_long_member_name.o" }, ArchiveMembers.Names(p));
        }
        finally { File.Delete(p); }
    }

    [Fact]
    public void NotAnArchive_IsNull()
    {
        var p = Path.Combine(Path.GetTempPath(), $"kq-ar-{Guid.NewGuid():N}.a");
        try
        {
            File.WriteAllText(p, "not an archive at all");
            Assert.Null(ArchiveMembers.Names(p));
            Assert.Null(ArchiveMembers.Names(p + ".missing"));
        }
        finally { File.Delete(p); }
    }
}
