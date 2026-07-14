using System;
using System.Speech.Synthesis;

namespace BgTtsVoicePatcher.Speech;

/// <summary>
/// Plays short live voice previews straight to the default audio device - lets the
/// user hear a SAPI voice before committing to it, without writing any file to disk.
/// Owns a single long-lived SpeechSynthesizer, reused across preview calls: disposing
/// and recreating one per call would risk cutting off audio that's still playing
/// asynchronously. Only one preview plays at a time - starting a new one cancels
/// whatever was still speaking.
/// </summary>
public sealed class VoicePreviewPlayer : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();

    public VoicePreviewPlayer()
    {
        _synth.SetOutputToDefaultAudioDevice();
    }

    /// <summary>Speaks sampleText using the given SAPI voice name, rate, and volume,
    /// cancelling any preview already in progress. Throws InvalidOperationException
    /// if the voice name doesn't exist.</summary>
    public void Preview(string voiceName, string sampleText, int rate, int volume)
    {
        try
        {
            _synth.SelectVoice(voiceName);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException($"No installed SAPI voice named '{voiceName}'.");
        }

        _synth.Rate = Math.Clamp(rate, -10, 10);
        _synth.Volume = Math.Clamp(volume, 0, 100);
        _synth.SpeakAsyncCancelAll();
        _synth.SpeakAsync(sampleText);
    }

    public void Stop() => _synth.SpeakAsyncCancelAll();

    public void Dispose() => _synth.Dispose();
}