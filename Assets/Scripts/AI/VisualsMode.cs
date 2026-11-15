using System;
using System.Text.RegularExpressions;
using UnityEngine;

// Non-visual mode: the app is used by ear, nothing of ours is drawn (VisualsGate hides it). Starts from
// AIConfig.nonVisualDefault. Switched by voice ("hey teacher, visuals off / on"), the radial menu (visual -> non-visual
// only, it is hidden afterwards) and F9 in the Editor.
public static class VisualsMode
{
    public static bool NonVisual { get; private set; }

    // nonVisual, source ("voice", "menu", "key", ...).
    public static event Action<bool, string> Changed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        NonVisual = AIConfig.Get().nonVisualDefault; // before AppFlow.Start, which skips calibration when it is on
        if (NonVisual) Debug.Log("[Visuals] Starting non-visual (AIConfig.nonVisualDefault).");
    }

    public static void Set(bool nonVisual, string source)
    {
        if (NonVisual == nonVisual) return;
        NonVisual = nonVisual;
        Debug.Log($"[Visuals] {(nonVisual ? "Off" : "On")} ({source}).");
        Changed?.Invoke(nonVisual, source);
    }

    public static void Toggle(string source) => Set(!NonVisual, source);

    // "visuals off", "turn the visuals on", "hide the visuals", "audio only", "non visual mode", "visual mode". Case and
    // punctuation are ignored, and the whole question must be the command, like QuestionRouter's other commands.
    public static bool TryParseCommand(string text, out bool visualsOn)
    {
        visualsOn = false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = Regex.Replace(text.ToLowerInvariant(), @"[^a-z\s]", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        t = Regex.Replace(t, @"^(?:(?:please|can you|could you|can we|let s|lets|ok|okay)\s+)*", "");
        t = Regex.Replace(t, @"\s+please$", "");
        var m = Command.Match(t);
        if (!m.Success) return false;
        visualsOn = m.Groups["on"].Success;
        return true;
    }

    static readonly Regex Command = new Regex(
        @"^(?:(?:turn|switch)\s+)?(?:the\s+)?visuals?\s+(?:mode\s+)?(?:(?<on>on)|off)$" +
        @"|^(?:turn|switch)\s+(?:(?<on>on)|off)\s+(?:the\s+)?visuals?$" +
        @"|^(?:(?<on>show)|hide)\s+(?:the\s+|me\s+the\s+)?visuals?$" +
        @"|^(?:non|no)\s+visuals?(?:\s+mode)?$|^(?:audio|voice)\s+only(?:\s+mode)?$" +
        @"|^(?<on>visuals?\s+mode)$");

    // The answer to the intro asks for voice only, anywhere in it: "yes, audio only", "no visuals please", "voice only".
    public static bool AsksForNonVisual(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = Regex.Replace(text.ToLowerInvariant(), @"[^a-z\s]", " ");
        return OffAnywhere.IsMatch(Regex.Replace(t, @"\s+", " "));
    }

    static readonly Regex OffAnywhere = new Regex(
        @"\b(?:(?:audio|voice|sound)\s+only|no\s+visuals?|visuals?\s+off|non\s+visual|without\s+(?:the\s+)?visuals?)\b");
}
