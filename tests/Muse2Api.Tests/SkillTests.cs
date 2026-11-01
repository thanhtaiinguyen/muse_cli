using System.IO.Compression;
using Muse2Api.Agent;

namespace Muse2Api.Tests;

public class SkillTests
{
    private static string WriteSource(TempDirectory dir, string name, string content)
    {
        var path = Path.Combine(dir.Root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ImportMarkdown_UsesFrontmatterAliasAndExpandsOnMention()
    {
        using var dir = new TempDirectory();
        var store = new SkillStore(Path.Combine(dir.Root, "skills"));
        var source = WriteSource(dir, "frontend.md", "---\nname: Frontend\nalias: fe\ndescription: Quy tắc giao diện\n---\n# Frontend\nLuôn dùng CSS grid.\n");
        var imported = store.Import(source);
        Assert.Equal("fe", imported.Alias);
        Assert.Equal("Frontend", imported.Name);
        Assert.Single(store.List());
        var block = store.Expand("@fe hãy dựng trang", out var applied);
        Assert.Equal(new[] { "fe" }, applied);
        Assert.Contains("CSS grid", block);
        Assert.Contains("@fe", block);
    }

    [Fact]
    public void ImportMarkdown_FallsBackToFileNameAlias()
    {
        using var dir = new TempDirectory();
        var store = new SkillStore(Path.Combine(dir.Root, "skills"));
        var source = WriteSource(dir, "my-review.md", "# Review\nKiểm tra bảo mật trước khi merge.\n");
        var imported = store.Import(source);
        Assert.Equal("my-review", imported.Alias);
        var block = store.Expand("nhờ @my-review xem giúp", out var applied);
        Assert.Equal(new[] { "my-review" }, applied);
        Assert.Contains("bảo mật", block);
    }

    [Fact]
    public void Mentions_IgnoresEmailsAndDuplicates()
    {
        Assert.Equal(new[] { "fe" }, SkillStore.Mentions("mail a@fe.com rồi @fe và @FE nữa"));
        Assert.Empty(SkillStore.Mentions("không có mention nào"));
        Assert.Equal(new[] { "fe", "api" }, SkillStore.Mentions("@fe trước, @api sau"));
    }

    [Fact]
    public void Expand_SkipsDisabledAndUnknownSkills()
    {
        using var dir = new TempDirectory();
        var store = new SkillStore(Path.Combine(dir.Root, "skills"));
        store.Import(WriteSource(dir, "fe.md", "# FE\nNội dung FE.\n"));
        store.SetEnabled("fe", false);
        var block = store.Expand("@fe @unknown làm việc", out var applied);
        Assert.Empty(applied);
        Assert.Equal("", block);
        store.SetEnabled("fe", true);
        block = store.Expand("@fe làm việc", out applied);
        Assert.Equal(new[] { "fe" }, applied);
        Assert.Contains("Nội dung FE", block);
    }

    [Fact]
    public void ImportZip_ReadsSkillFolderWithExtraFiles()
    {
        using var dir = new TempDirectory();
        var store = new SkillStore(Path.Combine(dir.Root, "skills"));
        var zipPath = Path.Combine(dir.Root, "api-skill.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("my-skill/SKILL.md");
            using (var writer = new StreamWriter(entry.Open())) writer.Write("---\nname: API\nalias: api\ndescription: Quy tắc API\n---\nDùng REST và JSON.\n");
            var extra = zip.CreateEntry("my-skill/notes.txt");
            using (var writer = new StreamWriter(extra.Open())) writer.Write("ghi chú");
        }
        var imported = store.Import(zipPath);
        Assert.Equal("api", imported.Alias);
        Assert.True(imported.IsFolder);
        Assert.True(File.Exists(Path.Combine(dir.Root, "skills", "api", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(dir.Root, "skills", "api", "notes.txt")));
        var block = store.Expand("@api thiết kế endpoint", out var applied);
        Assert.Equal(new[] { "api" }, applied);
        Assert.Contains("REST", block);
    }

    [Fact]
    public void ImportZip_WithoutSkillMd_Throws()
    {
        using var dir = new TempDirectory();
        var store = new SkillStore(Path.Combine(dir.Root, "skills"));
        var zipPath = Path.Combine(dir.Root, "bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("không có skill");
        }
        Assert.Throws<InvalidOperationException>(() => store.Import(zipPath));
    }

    [Fact]
    public void Import_RejectsUnsupportedExtension_AndRemoveDeletes()
    {
        using var dir = new TempDirectory();
        var store = new SkillStore(Path.Combine(dir.Root, "skills"));
        var exe = WriteSource(dir, "tool.exe", "binary");
        Assert.Throws<InvalidOperationException>(() => store.Import(exe));
        store.Import(WriteSource(dir, "fe.md", "# FE\nNội dung.\n"));
        Assert.True(store.Remove("fe"));
        Assert.Empty(store.List());
        Assert.False(store.Remove("fe"));
    }

    [Fact]
    public void SlugifyAndAliasValidation()
    {
        Assert.Equal("frontend-pro", SkillStore.Slugify("Frontend Pro!"));
        Assert.True(SkillStore.IsValidAlias("fe"));
        Assert.True(SkillStore.IsValidAlias("my-skill_2"));
        Assert.False(SkillStore.IsValidAlias("Fe"));
        Assert.False(SkillStore.IsValidAlias("-fe"));
        Assert.False(SkillStore.IsValidAlias(""));
    }
}
