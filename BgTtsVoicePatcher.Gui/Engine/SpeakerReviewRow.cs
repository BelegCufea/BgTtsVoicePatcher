using System;

namespace BgTtsVoicePatcher.Gui.Engine;

/// <summary>
/// One row of the Speaker Review grid's underlying data - richer than Core's
/// DialogReportRow (used for the CSV `report` output) because it also carries the
/// cleaned/spoken text (after token replacement and phonetic regex rules, or the
/// user's own hand-edited override) alongside the original raw dialogue text.
/// </summary>
public sealed record SpeakerReviewRow(
    int StrRef,
    string? SystemName,
    string? RealName,
    string Gender,
    bool HasSound,
    string? SoundResRef,
    bool? SoundFileExists,
    string RawText,
    string CleanedText,
    bool IsTextOverridden);
