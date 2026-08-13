#!/usr/bin/env dotnet
// i18n-convert — mockup JS i18n dictionaries → .resx converter (Stage S2.2).
//
// Usage (run from anywhere; input/output paths resolve against the repo root,
// i.e. the parent of the tools/ directory this file lives in):
//
//   dotnet run tools/i18n-convert.cs -- shared
//       Parses mockups/assets/js/i18n-tr.js + i18n-en.js and writes
//       src/RapidsolDestek.Web/Resources/SharedResources.resx   (TR = neutral)
//       src/RapidsolDestek.Web/Resources/SharedResources.en.resx (EN)
//
//   dotnet run tools/i18n-convert.cs -- page <mockup-html> <view-path> [<view-path2> ...]
//       Extracts the inline `window.PAGE_I18N = { tr: {...}, en: {...} };`
//       block from the mockup HTML and writes, for EACH view path (relative
//       under Resources/, no extension),
//       src/RapidsolDestek.Web/Resources/<view-path>.resx (+ .en.resx).
//
// TR is the neutral culture. If the TR and EN key sets differ, the tool lists
// every difference and exits 1 without writing anything. Keys keep their
// dotted names verbatim (e.g. "nav.group.workspace").

using System.Text;
using System.Text.RegularExpressions;

// One dictionary entry per line:  "dotted.key": "value with \" escapes",
var entryRx = new Regex(
    "^\\s*\"((?:[^\"\\\\]|\\\\.)+)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*,?\\s*$",
    RegexOptions.Multiline);

string root = RepoRoot();
string resourcesDir = Path.Combine(root, "src", "RapidsolDestek.Web", "Resources");

if (args.Length == 0)
    return Usage();

switch (args[0])
{
    case "shared":
        return RunShared();
    case "page" when args.Length >= 3:
        return RunPage(args[1], args[2..]);
    default:
        return Usage();
}

int RunShared()
{
    var tr = ParseEntries(ReadInput(Path.Combine("mockups", "assets", "js", "i18n-tr.js")), "i18n-tr.js");
    var en = ParseEntries(ReadInput(Path.Combine("mockups", "assets", "js", "i18n-en.js")), "i18n-en.js");
    if (!KeySetsMatch(tr, en, "shared dictionaries"))
        return 1;

    WriteResx(Path.Combine(resourcesDir, "SharedResources.resx"), tr);
    WriteResx(Path.Combine(resourcesDir, "SharedResources.en.resx"), en);
    Console.WriteLine($"shared: {tr.Count} keys -> SharedResources.resx + SharedResources.en.resx");
    return 0;
}

int RunPage(string htmlPath, string[] viewPaths)
{
    string html = ReadInput(htmlPath);
    var block = Regex.Match(html, @"window\.PAGE_I18N\s*=\s*(\{[\s\S]*?\});");
    if (!block.Success)
    {
        Console.Error.WriteLine($"error: no window.PAGE_I18N block found in {htmlPath}");
        return 1;
    }

    var tr = ParseLangSection(block.Groups[1].Value, "tr", htmlPath);
    var en = ParseLangSection(block.Groups[1].Value, "en", htmlPath);
    if (tr is null || en is null)
        return 1;
    if (!KeySetsMatch(tr, en, htmlPath))
        return 1;

    foreach (string view in viewPaths)
    {
        // View paths are relative under Resources/ and carry no extension.
        string clean = view.Replace('\\', '/').Trim('/');
        WriteResx(Path.Combine(resourcesDir, clean + ".resx"), tr);
        WriteResx(Path.Combine(resourcesDir, clean + ".en.resx"), en);
        Console.WriteLine($"page: {tr.Count} keys -> Resources/{clean}.resx + .en.resx");
    }
    return 0;
}

Dictionary<string, string>? ParseLangSection(string block, string lang, string source)
{
    var m = Regex.Match(block, $@"\b{lang}\s*:\s*\{{([\s\S]*?)\}}");
    if (!m.Success)
    {
        Console.Error.WriteLine($"error: no '{lang}:' section inside PAGE_I18N of {source}");
        return null;
    }
    return ParseEntries(m.Groups[1].Value, $"{source} [{lang}]");
}

Dictionary<string, string> ParseEntries(string text, string source)
{
    var dict = new Dictionary<string, string>();
    foreach (Match m in entryRx.Matches(text))
    {
        string key = Unescape(m.Groups[1].Value);
        if (dict.ContainsKey(key))
            Console.Error.WriteLine($"warning: duplicate key \"{key}\" in {source} (last value wins)");
        dict[key] = Unescape(m.Groups[2].Value);
    }
    if (dict.Count == 0)
        Console.Error.WriteLine($"warning: no entries parsed from {source}");
    return dict;
}

bool KeySetsMatch(Dictionary<string, string> tr, Dictionary<string, string> en, string what)
{
    var trOnly = tr.Keys.Where(k => !en.ContainsKey(k)).ToList();
    var enOnly = en.Keys.Where(k => !tr.ContainsKey(k)).ToList();
    foreach (string k in trOnly)
        Console.Error.WriteLine($"TR-only key in {what}: {k}");
    foreach (string k in enOnly)
        Console.Error.WriteLine($"EN-only key in {what}: {k}");
    if (trOnly.Count > 0 || enOnly.Count > 0)
    {
        Console.Error.WriteLine($"error: TR/EN key sets differ in {what} ({trOnly.Count} TR-only, {enOnly.Count} EN-only) — nothing written");
        return false;
    }
    return true;
}

void WriteResx(string path, Dictionary<string, string> entries)
{
    var sb = new StringBuilder();
    sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n");
    foreach (var (name, value) in new[]
    {
        ("resmimetype", "text/microsoft-resx"),
        ("version", "2.0"),
        ("reader", "System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"),
        ("writer", "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"),
    })
    {
        sb.Append($"  <resheader name=\"{name}\">\n    <value>{XmlEscape(value)}</value>\n  </resheader>\n");
    }
    foreach (var (key, value) in entries)
    {
        sb.Append($"  <data name=\"{XmlAttrEscape(key)}\" xml:space=\"preserve\">\n");
        sb.Append($"    <value>{XmlEscape(value)}</value>\n  </data>\n");
    }
    sb.Append("</root>\n");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
}

string ReadInput(string path)
{
    // Prefer repo-root-relative; fall back to as-given (absolute or cwd-relative).
    string rooted = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
    if (File.Exists(rooted))
        return File.ReadAllText(rooted);
    if (File.Exists(path))
        return File.ReadAllText(path);
    Console.Error.WriteLine($"error: input file not found: {path}");
    Environment.Exit(1);
    return "";
}

// JS string-literal unescaping: \" \\ \/ \n \r \t \b \f \0 \uXXXX.
static string Unescape(string s)
{
    if (!s.Contains('\\'))
        return s;
    var sb = new StringBuilder(s.Length);
    for (int i = 0; i < s.Length; i++)
    {
        if (s[i] != '\\' || i + 1 >= s.Length)
        {
            sb.Append(s[i]);
            continue;
        }
        char c = s[++i];
        switch (c)
        {
            case 'n': sb.Append('\n'); break;
            case 'r': sb.Append('\r'); break;
            case 't': sb.Append('\t'); break;
            case 'b': sb.Append('\b'); break;
            case 'f': sb.Append('\f'); break;
            case '0': sb.Append('\0'); break;
            case 'u' when i + 4 < s.Length
                      && ushort.TryParse(s.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out ushort cp):
                sb.Append((char)cp);
                i += 4;
                break;
            default: sb.Append(c); break; // covers \" \\ \/ and anything unknown
        }
    }
    return sb.ToString();
}

static string XmlEscape(string s) =>
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

static string XmlAttrEscape(string s) =>
    XmlEscape(s).Replace("\"", "&quot;");

static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
    Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, ".."));

static int Usage()
{
    Console.Error.WriteLine("""
        i18n-convert — mockup JS i18n dictionaries -> .resx (TR neutral + EN)
        usage:
          dotnet run tools/i18n-convert.cs -- shared
          dotnet run tools/i18n-convert.cs -- page <mockup-html> <view-path> [<view-path2> ...]
        """);
    return 2;
}
