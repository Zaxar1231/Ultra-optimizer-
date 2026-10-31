// Self-update from GitHub Releases: check latest -> download UltimateOptimizer.exe -> verify SHA-256 ->
// swap the running exe (Windows allows renaming a running file) -> restart.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

class ReleaseInfo
{
    public Version Version;
    public string Tag, Notes, PageUrl, ExeUrl, Sha256, SumsUrl;
    public long Size;
}

static class Updater
{
    public const string Repo = "Zaxar1231/Ultra-optimizer-";
    const string AssetName = "UltimateOptimizer.exe";
    const string Key = @"Software\UltimateOptimizer";

    public static Version Current { get { return new Version(BuildInfo.Version); } }

    public static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }

    public static bool AutoCheck
    {
        get
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) { object v = k == null ? null : k.GetValue("AutoUpdate"); return !(v is int) || (int)v != 0; } }
            catch { return true; }
        }
        set { try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("AutoUpdate", value ? 1 : 0, RegistryValueKind.DWord); } catch { } }
    }

    static void EnableTls()
    {
        // the exe targets the 4.x runtime without a TargetFramework quirk -> TLS 1.2 must be switched on explicitly
        try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)768; } catch { }
    }

    static HttpWebRequest Request(string url)
    {
        var r = (HttpWebRequest)WebRequest.Create(url);
        r.UserAgent = "UltimateOptimizer/" + BuildInfo.Version;
        r.Accept = "application/vnd.github+json, application/octet-stream, */*";
        r.Timeout = 20000;
        r.ReadWriteTimeout = 60000;
        r.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        return r;
    }

    static string GetString(string url)
    {
        using (var resp = Request(url).GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream()))
            return sr.ReadToEnd();
    }

    /// Latest published release, or throws.
    /// The REST API allows only 60 anonymous requests per hour per IP (shared IPs hit that quickly),
    /// so on any API error fall back to the releases/latest redirect + direct download links.
    public static ReleaseInfo Latest()
    {
        EnableTls();
        try { return Parse(GetString("https://api.github.com/repos/" + Repo + "/releases/latest")); }
        catch (WebException) { return LatestViaRedirect(); }
    }

    static ReleaseInfo LatestViaRedirect()
    {
        var req = Request("https://github.com/" + Repo + "/releases/latest");
        req.AllowAutoRedirect = false;
        req.Method = "HEAD";
        string location;
        using (var resp = (HttpWebResponse)req.GetResponse()) location = resp.Headers["Location"];
        var m = Regex.Match(location ?? "", @"/releases/tag/(v?\d+(\.\d+){1,3})$");
        if (!m.Success) throw new InvalidDataException("не удалось узнать последнюю версию");
        string tag = m.Groups[1].Value, dl = "https://github.com/" + Repo + "/releases/download/" + tag + "/";
        return new ReleaseInfo
        {
            Tag = tag, Version = new Version(tag.TrimStart('v')), Notes = "", PageUrl = "https://github.com/" + Repo + "/releases/tag/" + tag,
            ExeUrl = dl + AssetName, SumsUrl = dl + "SHA256SUMS.txt"
        };
    }

    public static ReleaseInfo Parse(string text)
    {
        var json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(text) as Dictionary<string, object>;
        if (json == null) throw new InvalidDataException("bad GitHub response");
        var r = new ReleaseInfo
        {
            Tag = Convert.ToString(json["tag_name"]),
            Notes = json.ContainsKey("body") ? Convert.ToString(json["body"]) : "",
            PageUrl = Convert.ToString(json["html_url"])
        };
        r.Version = new Version(Regex.Match(r.Tag, @"\d+(\.\d+){1,3}").Value);
        var assets = json["assets"] as object[];
        if (assets != null)
            foreach (Dictionary<string, object> a in assets)
            {
                string name = Convert.ToString(a["name"]);
                if (name == AssetName)
                {
                    r.ExeUrl = Convert.ToString(a["browser_download_url"]);
                    r.Size = Convert.ToInt64(a["size"]);
                    object digest;
                    if (a.TryGetValue("digest", out digest) && digest != null)
                    {
                        var m = Regex.Match(Convert.ToString(digest), "^sha256:([0-9a-fA-F]{64})$");
                        if (m.Success) r.Sha256 = m.Groups[1].Value.ToLowerInvariant();
                    }
                }
                else if (name == "SHA256SUMS.txt") r.SumsUrl = Convert.ToString(a["browser_download_url"]);
            }
        if (r.ExeUrl == null) throw new InvalidDataException("в релизе " + r.Tag + " нет " + AssetName);
        return r;
    }

    public static bool IsNewer(ReleaseInfo r) { return r != null && r.Version > Current; }

    /// Downloads, verifies and swaps the exe. Returns the path to start. progress: 0..100
    public static string Install(ReleaseInfo r, Action<int> progress)
    {
        EnableTls();
        string expected = r.Sha256;
        if (expected == null && r.SumsUrl != null)
        {
            var m = Regex.Match(GetString(r.SumsUrl), @"(?im)^([0-9a-f]{64})\s+\*?" + Regex.Escape(AssetName) + @"\s*$");
            if (m.Success) expected = m.Groups[1].Value.ToLowerInvariant();
        }
        if (expected == null) throw new InvalidDataException("у релиза нет контрольной суммы — обновление отменено");

        string exe = ExePath;
        string tmp = exe + ".download";
        using (var resp = Request(r.ExeUrl).GetResponse())
        using (var src = resp.GetResponseStream())
        using (var dst = File.Create(tmp))
        {
            long total = resp.ContentLength > 0 ? resp.ContentLength : r.Size, done = 0;
            var buf = new byte[81920];
            int n, last = -1;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                done += n;
                int pct = total > 0 ? (int)(done * 100 / total) : 0;
                if (pct != last && progress != null) { progress(pct); last = pct; }
            }
        }

        string got;
        using (var sha = SHA256.Create())
        using (var f = File.OpenRead(tmp))
            got = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
        if (got != expected)
        {
            TryDelete(tmp);
            throw new InvalidDataException("контрольная сумма не совпала — файл удалён");
        }
        try
        {
            if (AssemblyName.GetAssemblyName(tmp).Name != Assembly.GetExecutingAssembly().GetName().Name)
                throw new InvalidDataException("скачан не тот файл");
        }
        catch (BadImageFormatException) { TryDelete(tmp); throw new InvalidDataException("скачанный файл повреждён"); }

        // a running exe can be renamed but not overwritten
        string old = exe + ".old";
        TryDelete(old);
        try
        {
            File.Move(exe, old);
            File.Move(tmp, exe);
            return exe;
        }
        catch
        {
            // folder not writable / file locked -> keep the new version next to the old one
            if (File.Exists(old) && !File.Exists(exe)) File.Move(old, exe);
            string side = Path.Combine(Path.GetDirectoryName(exe), "UltimateOptimizer-" + r.Tag + ".exe");
            TryDelete(side);
            File.Move(tmp, side);
            return side;
        }
    }

    /// Called at startup: remove leftovers of the previous update.
    public static void Cleanup()
    {
        TryDelete(ExePath + ".old");
        TryDelete(ExePath + ".download");
    }

    static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    /// After a self-update the new process waits until the old one has exited.
    public static void WaitForParent(string pid)
    {
        int id;
        if (!int.TryParse(pid, out id)) return;
        try { using (var p = Process.GetProcessById(id)) p.WaitForExit(15000); } catch { }
    }

    /// Markdown release notes -> short plain text for the dialog.
    public static string PlainNotes(string md, int max)
    {
        if (string.IsNullOrEmpty(md)) return "";
        string s = md.Replace("\r", "");
        int cut = s.IndexOf("\n---");
        if (cut > 0) s = s.Substring(0, cut);
        s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", "");           // images
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");        // links
        s = Regex.Replace(s, @"(?m)^#+\s*", "");                     // headings
        s = Regex.Replace(s, @"(?m)^\s*[-*]\s+", "• ");              // bullets
        s = s.Replace("**", "").Replace("`", "");
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        return s.Length > max ? s.Substring(0, max).TrimEnd() + "…" : s;
    }
}
