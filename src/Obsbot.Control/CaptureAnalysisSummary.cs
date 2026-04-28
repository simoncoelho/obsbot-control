namespace Obsbot.Control;

public sealed record CaptureAnalysisSummary(
    string FileName,
    long Bytes,
    int CandidateControlTransfers,
    IReadOnlyList<string> Notes);
