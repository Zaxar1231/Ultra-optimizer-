// Tweak catalog (ui/tweaks.json): groups of individually switchable tweaks + profiles (presets).
// Every id must exist in Ultimate-Optimizer.ps1 as  if (Use 'id')  - build.ps1 checks this.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Win32;

class TweakItem
{
    public string Id, Title, Desc;
    public bool Default, LowRam;
    public TweakGroup Group;
}

class TweakGroup
{
    public string Id, Title, Icon;
    public bool Risk;
    public List<TweakItem> Items = new List<TweakItem>();
}

class TweakProfile
{
    public string Id, Title, Icon, Desc;
    public HashSet<string> Exclude = new HashSet<string>(), Include = new HashSet<string>();
}

class Catalog
{
    public List<TweakGroup> Groups = new List<TweakGroup>();
    public List<TweakProfile> Profiles = new List<TweakProfile>();
    public IEnumerable<TweakItem> Items { get { return Groups.SelectMany(g => g.Items); } }

    const string Key = @"Software\UltimateOptimizer";

    static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
    static bool B(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
    static IEnumerable<Dictionary<string, object>> L(Dictionary<string, object> d, string k)
    {
        object v;
        return d.TryGetValue(k, out v) && v is object[] ? ((object[])v).OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
    }
    static IEnumerable<string> Ids(Dictionary<string, object> d, string k)
    {
        object v;
        return d.TryGetValue(k, out v) && v is object[] ? ((object[])v).Select(Convert.ToString) : Enumerable.Empty<string>();
    }

    public static Catalog Load()
    {
        string json;
        using (var s = Program.Resource("ui/tweaks.json"))
        using (var r = new StreamReader(s)) json = r.ReadToEnd();
        var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
        var c = new Catalog();
        foreach (var g in L(root, "groups"))
        {
            var grp = new TweakGroup { Id = S(g, "id"), Title = S(g, "title"), Icon = Glyph(S(g, "icon")), Risk = B(g, "risk") };
            foreach (var i in L(g, "items"))
                grp.Items.Add(new TweakItem { Id = S(i, "id"), Title = S(i, "title"), Desc = S(i, "desc"), Default = B(i, "default"), LowRam = B(i, "lowRam"), Group = grp });
            c.Groups.Add(grp);
        }
        foreach (var p in L(root, "profiles"))
        {
            var pr = new TweakProfile { Id = S(p, "id"), Title = S(p, "title"), Icon = Glyph(S(p, "icon")), Desc = S(p, "desc") };
            foreach (var id in Ids(p, "exclude")) pr.Exclude.Add(id);
            foreach (var id in Ids(p, "include")) pr.Include.Add(id);
            c.Profiles.Add(pr);
        }
        return c;
    }

    static string Glyph(string hex)
    {
        int cp;
        return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out cp) ? char.ConvertFromUtf32(cp) : "";
    }

    /// Defaults of the catalog, adjusted to the hardware (transparency off on <= 8 GB RAM).
    public HashSet<string> Defaults(bool lowRam)
    {
        return new HashSet<string>(Items.Where(i => i.Default || (i.LowRam && lowRam)).Select(i => i.Id));
    }

    public HashSet<string> ForProfile(TweakProfile p, bool lowRam)
    {
        var set = Defaults(lowRam);
        set.ExceptWith(p.Exclude);
        set.UnionWith(p.Include);
        return set;
    }

    /// The profile whose selection equals this set exactly, or null ("свой набор").
    public TweakProfile Match(HashSet<string> sel, bool lowRam)
    {
        return Profiles.FirstOrDefault(p => ForProfile(p, lowRam).SetEquals(sel));
    }

    // ---- remembered between runs (HKCU\Software\UltimateOptimizer\Tweaks)
    public static HashSet<string> LoadSelection()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                var v = k == null ? null : k.GetValue("Tweaks") as string;
                if (v != null) return new HashSet<string>(v.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }
        catch { }
        return null;
    }

    public static void SaveSelection(IEnumerable<string> ids)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Tweaks", string.Join(",", ids)); } catch { }
    }
}
