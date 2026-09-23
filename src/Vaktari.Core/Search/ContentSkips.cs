namespace Vaktari.Core.Search;

/// <summary>
/// What a search that reads contents would not read, counted while it walks.
///
/// **A file the search declined to read is a file it cannot say the text is
/// not in.** Name search never had this problem — every name is read — but a
/// content search refuses some files on purpose, and a refusal nobody hears
/// about reads as "not in there". So the refusals worth a sentence are counted
/// on an object the caller hands in through <see cref="SearchQuery.Skipped"/>,
/// and the band says the number.
///
/// **Two kinds are counted, and two are left out on purpose.** Too large and
/// kept online are counted, because either could hold the answer and a person
/// can do something about both: open the big file themselves, or make the
/// folder available offline. A binary file is not — its "text" is not text
/// anybody typed — and nor is one that could not be opened, which is hidden
/// from the person searching exactly as a folder they cannot open is, and the
/// walk has never counted those either.
///
/// Written from the walk's thread and read from the dispatcher, so every count
/// goes through Interlocked.
/// </summary>
public sealed class ContentSkips
{
    private int _tooLarge;
    private int _online;

    /// <summary>Files over <see cref="ContentMatcher.MaxBytes"/>, never opened.</summary>
    public int TooLarge => Volatile.Read(ref _tooLarge);

    /// <summary>
    /// Files whose contents are held by a cloud client rather than on the disk,
    /// never opened because opening one downloads it. Only the Windows walk can
    /// see this; elsewhere it stays zero.
    /// </summary>
    public int Online => Volatile.Read(ref _online);

    public void CountTooLarge() => Interlocked.Increment(ref _tooLarge);

    public void CountOnline() => Interlocked.Increment(ref _online);
}
