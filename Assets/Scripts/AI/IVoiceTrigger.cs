using System;

// Something that starts a spoken question: the "hey teacher" wake word, the Q key, a right-hand pinch-hold.
public interface IVoiceTrigger
{
    string Name { get; }
    bool IsActive { get; }         // usable on this platform right now
    void SetArmed(bool armed);     // false = ignore input (pause)
    event Action<IVoiceTrigger> Triggered;
}

// Records one question after a trigger and ends it on silence.
public interface IQuestionRecorder
{
    bool IsRecording { get; }
    void BeginQuestion();
    void Cancel();                 // no QuestionRecorded follows
    event Action<byte[]> QuestionRecorded; // 16 kHz mono WAV, or null when nothing was heard
}
