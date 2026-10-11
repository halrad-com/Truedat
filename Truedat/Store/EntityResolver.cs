using System;

namespace Truedat.Store
{
    internal enum ResolveHow { MatchedHash, MatchedPath, Created, CreatedNoSha }

    internal sealed class ResolveResult
    {
        public string EntityId = "";
        public ResolveHow How;
    }

    /// <summary>Facet kinds — each names its algorithm, so a second hash can sit beside the first and a
    /// key move is always recoverable from the store (the 2026-10-11 FLAC-residuals lesson).</summary>
    internal static class FacetKinds
    {
        internal const string ShaFrames = "content_hash:audio-stream-sha256";                       // current primary (FLAC frame-anchored since 2026-07-21; TagLib invariant region otherwise)
        internal const string ShaTaglibRegion = "content_hash:audio-stream-sha256:taglib-region";   // pre-2026-07-21 FLAC value
        internal const string ShaWholeFile = "content_hash:audio-stream-sha256:whole-file";         // fallback when invariant bounds were unavailable
        internal const string PathCanonical = "path:canonical";
        internal const string ContentHashPrefix = "content_hash:";

        /// <summary>Scheme precedence: a newer scheme is never demoted by an older one arriving later
        /// (review #4a — an old mirror must not promote a taglib-region sha over the frames sha).</summary>
        internal static int Rank(string kind) => kind == ShaFrames ? 2 : kind == ShaWholeFile ? 1 : 0;

        /// <summary>Maps the catalog's <c>audioStreamSha256Source</c> to a facet kind. A FLAC with no
        /// source tag was hashed before 2026-07-21 (TagLib region); anything else without a tag is the
        /// invariant-region hash, which is the current algorithm for non-FLAC.</summary>
        internal static string ForShaSource(string? source, bool isFlac)
        {
            if (source == "flac-frames") return ShaFrames;
            if (source == "whole-file") return ShaWholeFile;
            return isFlac ? ShaTaglibRegion : ShaFrames;
        }
    }

    /// <summary>SST matching tiers as S1 needs them: exact content-hash facet → exact (root, canonical
    /// path) → create. Idempotent on content hash (SST OQ2): the same hash never mints twice. A path
    /// match with a new hash records the new hash as primary and demotes the old one, never deleting
    /// it. Conflict detection (same hash, different audio props) is the import's job — it has the
    /// feature rows to compare.</summary>
    internal static class EntityResolver
    {
        /// <param name="matchPathAcrossRoots">For a root the operator declared a MIRROR: the same canonical
        /// path under any root is the same file (X:\Library mirrors D:\Music), so a no-sha entry still
        /// resolves. Never on for a primary import — two unrelated libraries can share a relative path.</param>
        internal static ResolveResult Resolve(StoreDal dal, string? contentHash, string shaKind, long rootId, string canonicalPath, string source, string nowUtc, bool matchPathAcrossRoots = false)
        {
            if (!string.IsNullOrEmpty(contentHash))
            {
                var byHash = dal.FindEntityByFacet(shaKind, contentHash!);
                if (byHash != null)
                {
                    dal.TouchFacet(byHash, shaKind, contentHash!, nowUtc);
                    return new ResolveResult { EntityId = byHash, How = ResolveHow.MatchedHash };
                }
            }

            dal.FindLocation(rootId, canonicalPath, out var byPath);
            if (byPath == null && matchPathAcrossRoots) dal.FindLocationAnyRoot(canonicalPath, out byPath);
            if (byPath != null)
            {
                if (!string.IsNullOrEmpty(contentHash))
                {
                    var current = dal.PrimaryContentHashKind(byPath);
                    if (current != null && FacetKinds.Rank(current) > FacetKinds.Rank(shaKind))
                    {
                        // An older scheme arriving for a path the newer scheme already keys: record it, never promote it.
                        dal.UpsertFacet(byPath, shaKind, contentHash!, isPrimary: false, confidence: 1.0, source, nowUtc);
                        StoreLog.Debug($"path {canonicalPath}: {shaKind} {contentHash} recorded as non-primary under the newer {current}");
                    }
                    else
                    {
                        dal.DemoteFacets(byPath, FacetKinds.ContentHashPrefix);
                        dal.UpsertFacet(byPath, shaKind, contentHash!, isPrimary: true, confidence: 1.0, source, nowUtc);
                        StoreLog.Debug($"path {canonicalPath} re-keyed to {shaKind} {contentHash}; previous hash kept as a non-primary facet");
                    }
                }
                return new ResolveResult { EntityId = byPath, How = ResolveHow.MatchedPath };
            }

            var id = dal.CreateEntity(nowUtc);
            if (!string.IsNullOrEmpty(contentHash))
            {
                dal.UpsertFacet(id, shaKind, contentHash!, isPrimary: true, confidence: 1.0, source, nowUtc);
                return new ResolveResult { EntityId = id, How = ResolveHow.Created };
            }
            // No hash at all (the two MB6r entries): a path facet only, low confidence, never guessed.
            dal.UpsertFacet(id, FacetKinds.PathCanonical, canonicalPath, isPrimary: true, confidence: 0.5, source, nowUtc);
            return new ResolveResult { EntityId = id, How = ResolveHow.CreatedNoSha };
        }
    }
}
