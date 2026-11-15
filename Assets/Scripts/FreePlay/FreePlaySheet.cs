using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// Free play's sheet music: a song from the database (Assets/Resources/SongDB) with its generated strips
// (Assets/Resources/Sheets), in the lesson sheet's spot, one line of music at a time, the bar being played outlined.
// Nothing is taught or judged: no lit keys, no red, wrong notes are ignored (SheetFollower).
// Opens when the analyzer recognizes a song, or by voice ("hey teacher, show Jingle Bells"). A song asked for by voice
// stays until "close the music" or the end of free play; recognition does not replace it.
// FreePlayController adds this at runtime. Reads SongNoteDisplay's transform and size only, so the lesson panel is untouched.
public class FreePlaySheet : MonoBehaviour
{
    public SongNoteDisplay lessonSheet;
    public Color pageColor = Color.white;
    public Color frameColor = new Color(0.05f, 0.45f, 1f, 1f);
    public Color titleColor = new Color(0.85f, 0.95f, 1f, 1f);

    const float FrameThickness = 5f, TitleHeight = 60f;

    public bool IsShowing => _song != null;
    public string SongId => _song?.id;
    public bool PinnedByVoice { get; private set; }
    public int CurrentBar => _follower?.CurrentBar ?? 0;

    SongData _song;
    SheetMusic _sheet;
    SheetFollower _follower;
    int _shownSystem = -1;

    Transform _holder;
    RectTransform _root, _strip;
    RawImage _image;
    TextMeshProUGUI _title;
    readonly Image[] _frame = new Image[4];

    void Awake()
    {
        if (!lessonSheet) lessonSheet = FindAnyObjectByType<SongNoteDisplay>(FindObjectsInactive.Include);
    }

    // ------------------------------------------------------------------ Showing a song

    // False (and nothing changes) when the song or its sheet music is not there.
    public bool Show(string songId, bool byVoice, int transpose = 0)
    {
        var song = SongDatabase.Get(songId);
        var sheet = SheetLibrary.Get(songId);
        if (song == null || sheet == null || sheet.layout.systems == null || sheet.layout.systems.Count == 0 ||
            sheet.systems.Length == 0 || sheet.systems[0] == null)
        {
            Debug.LogWarning($"[FreePlaySheet] No song or sheet music for \"{songId}\".");
            return false;
        }

        if (!_root) Build();
        bool same = _song != null && _song.id == songId;
        _song = song;
        _sheet = sheet;
        PinnedByVoice = byVoice || (same && PinnedByVoice);
        if (!same) _follower = new SheetFollower(song);
        _follower.Transpose = transpose;

        var entry = SongDatabase.Entry(songId);
        _title.text = entry != null ? entry.name : song.name;
        _shownSystem = -1;
        Refresh();
        _holder.gameObject.SetActive(true);
        Place();
        Debug.Log($"[FreePlaySheet] Showing {_title.text} ({sheet.layout.systems.Count} line(s)){(PinnedByVoice ? ", asked for by voice" : "")}.");
        return true;
    }

    public void Hide()
    {
        if (_song == null) return;
        Debug.Log($"[FreePlaySheet] Hiding {_song.name}.");
        _song = null;
        _sheet = null;
        _follower = null;
        PinnedByVoice = false;
        if (_holder) _holder.gameObject.SetActive(false);
    }

    // A finished phrase: a recognized song opens (unless one was asked for by voice), and its notes set the position.
    public void OnPhrase(PhraseAnalysis p)
    {
        if (p?.song == null || string.IsNullOrEmpty(p.song.songId)) return;
        if (PinnedByVoice && p.song.songId != SongId) return;
        if (SongId != p.song.songId)
        {
            if (!Show(p.song.songId, false, p.song.transpose)) return;
            if (p.melody != null) foreach (int midi in p.melody) _follower.NoteOn(midi);
            Refresh();
        }
    }

    public void NoteOn(int midi)
    {
        if (_follower == null) return;
        if (_follower.NoteOn(midi)) Refresh();
    }

    // ------------------------------------------------------------------ Voice: "show Jingle Bells", "close the music"

    static readonly Regex ShowCommand = new Regex(
        @"^(?:show|open|pull up|bring up|put up|load|play|start|practice|learn|try)(?: me)?(?: the)?(?: (?:sheet music|music|sheet|notes|score)(?: for| of| to)?)?(?: the)? (?<song>.+)$");
    static readonly Regex CloseCommand = new Regex(
        @"^(?:close|hide|remove|put away|take away|clear)(?: the)? (?:sheet music|music|sheet|score|notes)$");

    // "show me the music for Jingle Bells" -> song "jingle bells". Case, accents and punctuation are ignored.
    public static bool TryParseShowCommand(string text, out string song)
    {
        song = null;
        var m = ShowCommand.Match(Normalize(StripPolite(text)));
        if (!m.Success) return false;
        song = m.Groups["song"].Value.Trim();
        return song.Length > 0 && !CloseCommand.IsMatch("close " + song); // "show the music" alone is not a song
    }

    public static bool IsCloseCommand(string text) => CloseCommand.IsMatch(Normalize(StripPolite(text)));

    static string StripPolite(string text)
    {
        string t = Normalize(text);
        t = Regex.Replace(t, @"^(?:(?:please|can you|could you|will you|can we|ok|okay|hey|now|lets|let s|i want to|i wanna|i d like to|i would like to|i want you to|i m going to|i ll)\s+)*", "");
        return Regex.Replace(t, @"\s+(?:please|for me)$", "");
    }

    static readonly HashSet<string> Filler = new HashSet<string> { "the", "a", "an", "to", "of", "song", "piece", "please", "by", "in", "for" };

    // The database song whose name (or id) has all of the asked words, allowing one letter off per word of 4+ letters; null if none.
    public static SongEntry FindSong(string asked, IEnumerable<SongEntry> songs)
    {
        var want = Words(asked).Where(w => !Filler.Contains(w)).ToList();
        if (want.Count == 0) return null;
        SongEntry best = null;
        float bestScore = 0f;
        foreach (var e in songs)
        {
            var have = Words(e.name).Concat(Words((e.id ?? "").Replace('_', ' '))).Distinct().ToList();
            int hits = want.Count(w => have.Any(h => Same(w, h)));
            if (hits < want.Count) continue; // every asked word must be there
            float score = hits / (float)Mathf.Max(1, Words(e.name).Count(w => !Filler.Contains(w))); // fewer extra words wins
            if (score > bestScore) { bestScore = score; best = e; }
        }
        return best;
    }

    static bool Same(string a, string b) => a == b || (a.Length >= 4 && b.Length >= 4 && Distance(a, b) <= 1);

    static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Mathf.Min(Mathf.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    static List<string> Words(string text) =>
        Normalize(text).Split(' ').Where(w => w.Length > 0).ToList();

    // Lower case, accents dropped ("Für" -> "fur"), anything but letters, digits and spaces removed.
    static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string decomposed;
        try { decomposed = text.Normalize(NormalizationForm.FormD); }
        catch (System.Exception) { decomposed = text; } // no Unicode tables on this runtime: the map below still works
        var sb = new StringBuilder();
        foreach (char c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(Fold(c));
        string t = Regex.Replace(sb.ToString().ToLowerInvariant(), @"[^a-z0-9\s]", " ");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    static char Fold(char c)
    {
        switch (char.ToLowerInvariant(c))
        {
            case 'à': case 'á': case 'â': case 'ä': return 'a';
            case 'è': case 'é': case 'ê': case 'ë': return 'e';
            case 'ì': case 'í': case 'î': case 'ï': return 'i';
            case 'ò': case 'ó': case 'ô': case 'ö': return 'o';
            case 'ù': case 'ú': case 'û': case 'ü': return 'u';
            case 'ç': return 'c';
            default: return c;
        }
    }

    // ------------------------------------------------------------------ Panel

    void LateUpdate()
    {
        if (_song != null) Place();
    }

    // The lesson sheet's spot and scale (it follows recalibration); without one, in front of the head once.
    void Place()
    {
        if (!_holder) return;
        if (lessonSheet)
        {
            var t = lessonSheet.transform;
            _holder.SetPositionAndRotation(t.position, t.rotation);
            _holder.localScale = t.lossyScale;
            return;
        }
        var cam = Camera.main ? Camera.main.transform : null;
        if (cam && _holder.position == Vector3.zero)
        {
            Vector3 forward = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            _holder.SetPositionAndRotation(cam.position + forward * 0.8f, Quaternion.LookRotation(forward, Vector3.up));
        }
    }

    float PanelWidth => lessonSheet ? lessonSheet.panelSize.x : 900f;
    float MetersPerUnit => lessonSheet ? lessonSheet.metersPerUnit : 0.001f;

    void Build()
    {
        _holder = new GameObject("FreePlaySheet").transform;
        _holder.SetParent(transform, false);

        var canvasGo = new GameObject("Free Play Sheet Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(_holder, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
        canvasGo.GetComponent<CanvasScaler>().dynamicPixelsPerUnit = 3f;
        _root = (RectTransform)canvasGo.transform;
        _root.localScale = Vector3.one * MetersPerUnit;

        var titleGo = new GameObject("Title", typeof(RectTransform), typeof(TextMeshProUGUI));
        Place(titleGo, _root, 0f, 0f, PanelWidth, TitleHeight);
        _title = titleGo.GetComponent<TextMeshProUGUI>();
        _title.fontSize = 34f;
        _title.alignment = TextAlignmentOptions.Center;
        _title.color = titleColor;
        _title.raycastTarget = false;

        var stripGo = new GameObject("Strip", typeof(RectTransform), typeof(RawImage));
        _strip = Place(stripGo, _root, 0f, TitleHeight, PanelWidth, 100f);
        _image = stripGo.GetComponent<RawImage>();
        _image.color = pageColor;
        _image.raycastTarget = false;
        for (int i = 0; i < 4; i++)
        {
            var go = new GameObject("Frame", typeof(RectTransform), typeof(Image));
            Place(go, _strip, 0f, 0f, 1f, 1f);
            _frame[i] = go.GetComponent<Image>();
            _frame[i].color = frameColor;
            _frame[i].raycastTarget = false;
        }
        _holder.gameObject.SetActive(false);
    }

    void ShowSystem(int index)
    {
        index = Mathf.Clamp(index, 0, _sheet.layout.systems.Count - 1);
        if (index == _shownSystem) return;
        _shownSystem = index;
        var info = _sheet.layout.systems[index];
        var tex = index < _sheet.systems.Length ? _sheet.systems[index] : null;
        _image.texture = tex;
        float height = PanelWidth * info.height / Mathf.Max(1f, info.width);
        _strip.sizeDelta = new Vector2(PanelWidth, height);
        _root.sizeDelta = new Vector2(PanelWidth, TitleHeight + height); // centred on the lesson sheet's spot
    }

    // Shows the line holding the current bar and outlines that bar; no outline before the first matched note.
    void Refresh()
    {
        if (_sheet == null) return;
        int bar = CurrentBar;
        var systems = _sheet.layout.systems;
        int index = bar > 0 ? systems.FindIndex(s => bar >= s.firstBar && bar <= s.lastBar) : 0;
        if (index < 0) index = Mathf.Max(0, _shownSystem);
        ShowSystem(index);

        var info = systems[_shownSystem];
        int i = bar - info.firstBar;
        bool show = bar > 0 && i >= 0 && i + 1 < info.barEdges.Length;
        foreach (var f in _frame) f.gameObject.SetActive(show);
        if (!show) return;
        float w = _strip.sizeDelta.x, h = _strip.sizeDelta.y, t = FrameThickness;
        float x = info.barEdges[i] * w, x1 = info.barEdges[i + 1] * w, y = info.top * h, bh = (info.bottom - info.top) * h;
        SetRect(_frame[0], x, y, x1 - x, t);
        SetRect(_frame[1], x, y + bh - t, x1 - x, t);
        SetRect(_frame[2], x, y, t, bh);
        SetRect(_frame[3], x1 - t, y, t, bh);
    }

    // Top-left layout, y down, in canvas units (like SongNoteDisplay).
    static RectTransform Place(GameObject go, RectTransform parent, float x, float y, float w, float h)
    {
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    static void SetRect(Image image, float x, float y, float w, float h)
    {
        var rt = (RectTransform)image.transform;
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }
}
