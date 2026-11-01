using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Muse2Api.Agent;

/// <summary>One installed skill: a Markdown instruction pack the user can
/// invoke in chat with @alias (for example @fe). Content comes from a file
/// the user imported (.md/.txt single file, or a .zip containing SKILL.md).</summary>
public sealed record SkillDefinition(string Alias, string Name, string Description, string Content, string SourcePath, bool IsFolder);

/// <summary>File-backed skill registry. Layout inside the store directory:
/// single-file skills live as &lt;alias&gt;.md, folder skills as
/// &lt;alias&gt;/SKILL.md, and skills.json keeps the disabled alias list.
/// Bad files are skipped on load instead of breaking the whole registry.</summary>
public sealed class SkillStore
{
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxZipBytes = 10 * 1024 * 1024;
    public const int MaxZipEntries = 200;
    public const int MaxContentChars = 12000;
    public const int MaxExpandedChars = 30000;
    private const string StateFile = "skills.json";
    private static readonly Regex MentionPattern = new(@"(?<![A-Za-z0-9_@])@([A-Za-z0-9][A-Za-z0-9_-]{0,39})", RegexOptions.Compiled);

    public string DirectoryPath { get; }

    public SkillStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Thiếu thư mục kỹ năng.");
        DirectoryPath = Path.GetFullPath(directory);
    }

    public static bool IsValidAlias(string? alias)
    {
        if (string.IsNullOrEmpty(alias) || alias.Length > 40) return false;
        if (!char.IsAsciiLetterLower(alias[0]) && !char.IsAsciiDigit(alias[0])) return false;
        return alias.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');
    }

    public static string Slugify(string? value)
    {
        var builder = new StringBuilder();
        var lastDash = true; // trims leading separators
        foreach (var raw in (value ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(raw) || char.IsAsciiDigit(raw) || raw is '_')
            {
                builder.Append(raw); lastDash = false;
            }
            else if (!lastDash) { builder.Append('-'); lastDash = true; }
            if (builder.Length >= 40) break;
        }
        return builder.ToString().Trim('-');
    }

    /// <summary>Splits a skill document into frontmatter metadata and body.
    /// Frontmatter is a leading --- block with simple key: value lines
    /// (name, description, alias). Without frontmatter the first "# " heading
    /// becomes the name and the file name becomes the alias.</summary>
    public static (string Alias, string Name, string Description, string Body) Parse(string text, string fallbackAlias)
    {
        var normalized = (text ?? "").Replace("\r\n", "\n");
        var alias = ""; var name = ""; var description = ""; var body = normalized;
        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                foreach (var line in normalized[4..end].Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    var key = line[..colon].Trim().ToLowerInvariant();
                    var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                    if (key == "alias" || key == "slug") alias = value;
                    else if (key == "name") name = value;
                    else if (key == "description") description = value;
                }
                var after = normalized[(end + 4)..];
                body = after.StartsWith("\n", StringComparison.Ordinal) ? after[1..] : after;
            }
        }
        if (name.Length == 0)
            foreach (var line in body.Split('\n'))
                if (line.StartsWith("# ", StringComparison.Ordinal)) { name = line[2..].Trim(); break; }
        if (description.Length == 0)
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal)) continue;
                description = trimmed.Length <= 160 ? trimmed : trimmed[..160]; break;
            }
        // Precedence: explicit frontmatter alias, then the file/folder name
        // the user chose, then the document title. A heading like "# Review"
        // inside my-review.md must not rename the skill to @review.
        var finalAlias = Slugify(alias.Length > 0 ? alias : fallbackAlias);
        if (!IsValidAlias(finalAlias)) finalAlias = Slugify(name);
        if (!IsValidAlias(finalAlias)) throw new InvalidOperationException("Không xác định được tên gọi tắt (alias) hợp lệ cho kỹ năng. Đặt frontmatter alias: hoặc tên file dạng chu-thuong, vi du fe.md.");
        return (finalAlias, name.Length > 0 ? name : finalAlias, description, body.Trim());
    }

    private string StatePath => Path.Combine(DirectoryPath, StateFile);

    private List<string> DisabledAliases()
    {
        try
        {
            if (!File.Exists(StatePath)) return new();
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StatePath)) ?? new();
        }
        catch { return new(); }
    }

    private void SaveDisabled(List<string> disabled)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(disabled.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
    }

    public bool IsEnabled(string alias) => !DisabledAliases().Contains(alias, StringComparer.OrdinalIgnoreCase);

    public void SetEnabled(string alias, bool enabled)
    {
        var disabled = DisabledAliases();
        disabled.RemoveAll(a => a.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (!enabled) disabled.Add(alias.ToLowerInvariant());
        SaveDisabled(disabled);
    }

    public List<SkillDefinition> List()
    {
        var result = new List<SkillDefinition>();
        if (!Directory.Exists(DirectoryPath)) return result;
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.md"))
        {
            if (Path.GetFileName(file).Equals(StateFile, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var parsed = Parse(File.ReadAllText(file), Path.GetFileNameWithoutExtension(file));
                result.Add(new(parsed.Alias, parsed.Name, parsed.Description, parsed.Body, file, false));
            }
            catch { /* skip a broken skill file */ }
        }
        foreach (var folder in Directory.EnumerateDirectories(DirectoryPath))
        {
            var skillFile = Directory.EnumerateFiles(folder).FirstOrDefault(f => Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
            if (skillFile is null) continue;
            try
            {
                var parsed = Parse(File.ReadAllText(skillFile), Path.GetFileName(folder));
                result.Add(new(parsed.Alias, parsed.Name, parsed.Description, parsed.Body, skillFile, true));
            }
            catch { /* skip a broken skill folder */ }
        }
        return result.GroupBy(s => s.Alias, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(s => s.Alias, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public SkillDefinition? Get(string alias)
        => List().FirstOrDefault(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

    /// <summary>Installs a skill from a user-picked file: .md/.txt single file
    /// (alias from frontmatter or file name) or .zip holding a SKILL.md.
    /// Importing an existing alias replaces the old version.</summary>
    public SkillDefinition Import(string sourcePath)
    {
        if (!File.Exists(sourcePath)) throw new InvalidOperationException("Không tìm thấy file kỹ năng đã chọn.");
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        Directory.CreateDirectory(DirectoryPath);
        if (extension is ".md" or ".txt")
        {
            if (new FileInfo(sourcePath).Length > MaxTextBytes) throw new InvalidOperationException("File kỹ năng vượt quá 1 MiB.");
            var text = File.ReadAllText(sourcePath);
            var parsed = Parse(text, Path.GetFileNameWithoutExtension(sourcePath));
            File.WriteAllText(Path.Combine(DirectoryPath, parsed.Alias + ".md"), text);
            RemoveFolderSkill(parsed.Alias);
            return Get(parsed.Alias) ?? new SkillDefinition(parsed.Alias, parsed.Name, parsed.Description, parsed.Body, Path.Combine(DirectoryPath, parsed.Alias + ".md"), false);
        }
        if (extension == ".zip") return ImportZip(sourcePath);
        throw new InvalidOperationException("Định dạng chưa hỗ trợ. Hãy chọn file .md, .txt hoặc gói .zip chứa SKILL.md.");
    }

    private SkillDefinition ImportZip(string sourcePath)
    {
        if (new FileInfo(sourcePath).Length > MaxZipBytes) throw new InvalidOperationException("Gói kỹ năng vượt quá 10 MiB.");
        using var archive = ZipFile.OpenRead(sourcePath);
        if (archive.Entries.Count > MaxZipEntries) throw new InvalidOperationException("Gói kỹ năng có quá nhiều file.");
        var skillEntry = archive.Entries
            .Where(e => Path.GetFileName(e.FullName).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c == '/')).FirstOrDefault()
            ?? throw new InvalidOperationException("Gói .zip phải chứa file SKILL.md.");
        var prefix = skillEntry.FullName[..(skillEntry.FullName.Length - Path.GetFileName(skillEntry.FullName).Length)];
        string text;
        using (var reader = new StreamReader(skillEntry.Open())) text = reader.ReadToEnd();
        var parsed = Parse(text, prefix.Trim('/').Split('/').LastOrDefault() ?? "skill");
        var temp = Path.Combine(DirectoryPath, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var relative = entry.FullName[prefix.Length..];
                if (relative.Length == 0 || relative.EndsWith("/", StringComparison.Ordinal)) continue;
                total += entry.Length;
                if (total > MaxZipBytes) throw new InvalidOperationException("Gói kỹ năng giải nén vượt quá 10 MiB.");
                var destination = Path.GetFullPath(Path.Combine(temp, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Path.GetFullPath(temp) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Gói kỹ năng chứa đường dẫn không an toàn.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = File.Create(destination);
                input.CopyTo(output);
            }
            var singleFile = Path.Combine(DirectoryPath, parsed.Alias + ".md");
            if (File.Exists(singleFile)) File.Delete(singleFile);
            RemoveFolderSkill(parsed.Alias);
            Directory.Move(temp, Path.Combine(DirectoryPath, parsed.Alias));
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
        return Get(parsed.Alias) ?? throw new InvalidOperationException("Đã giải nén gói nhưng không đọc lại được kỹ năng.");
    }

    private void RemoveFolderSkill(string alias)
    {
        var folder = Path.Combine(DirectoryPath, alias);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    public bool Remove(string alias)
    {
        var removed = false;
        var file = Path.Combine(DirectoryPath, alias + ".md");
        if (File.Exists(file)) { File.Delete(file); removed = true; }
        if (Directory.Exists(Path.Combine(DirectoryPath, alias))) { RemoveFolderSkill(alias); removed = true; }
        if (removed) SetEnabled(alias, true); // a future re-import starts enabled
        return removed;
    }

    /// <summary>Finds @alias mentions in a chat prompt, in order, de-duplicated.</summary>
    public static IReadOnlyList<string> Mentions(string? text)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text)) return found;
        foreach (Match match in MentionPattern.Matches(text))
        {
            var alias = match.Groups[1].Value.ToLowerInvariant();
            if (!found.Contains(alias)) found.Add(alias);
        }
        return found;
    }

    /// <summary>Builds the instruction block for the skills a prompt invokes
    /// with @alias. Unknown or disabled skills are ignored.</summary>
    public string Expand(string prompt, out IReadOnlyList<string> applied)
    {
        var used = new List<string>();
        var blocks = new List<string>();
        var budget = MaxExpandedChars;
        foreach (var alias in Mentions(prompt))
        {
            var skill = Get(alias);
            if (skill is null || !IsEnabled(skill.Alias)) continue;
            var content = skill.Content.Length <= MaxContentChars ? skill.Content : skill.Content[..MaxContentChars] + "\n[... nội dung kỹ năng đã được rút gọn ...]";
            var block = "### Kỹ năng @" + skill.Alias + " — " + skill.Name
                + (skill.Description.Length > 0 ? "\n" + skill.Description : "") + "\n\n" + content;
            if (block.Length > budget) break;
            budget -= block.Length;
            blocks.Add(block); used.Add(skill.Alias);
        }
        applied = used;
        if (blocks.Count == 0) return "";
        return "CÁC KỸ NĂNG NGƯỜI DÙNG YÊU CẦU ÁP DỤNG CHO TÁC VỤ NÀY (@alias trong yêu cầu). "
            + "Hãy làm theo nội dung từng kỹ năng; quy tắc duyệt thao tác và an toàn của ứng dụng vẫn được giữ nguyên.\n\n"
            + string.Join("\n\n", blocks);
    }
}
