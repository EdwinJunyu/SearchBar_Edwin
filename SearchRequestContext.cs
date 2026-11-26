namespace ESearch;

// A query captures its identity once. Callers supply live UI state only when publishing.
// Catalog edits invalidate dictionary work without invalidating the current file search.
internal readonly record struct SearchRequestContext(long Revision, long CatalogRevision, CancellationToken Token)
{
    internal bool IsCurrent(long revision, bool visible) =>
        !Token.IsCancellationRequested && Revision == revision && visible;

    internal bool IsCatalogCurrent(long revision, long catalogRevision, bool visible) =>
        IsCurrent(revision, visible) && CatalogRevision == catalogRevision;

    internal bool CanPublishSuggestions(long revision, long catalogRevision, bool visible, bool composing) =>
        IsCatalogCurrent(revision, catalogRevision, visible) && !composing;
}
