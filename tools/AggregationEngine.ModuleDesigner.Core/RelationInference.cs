using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AggregationEngine.ModuleDesigner.Core
{
    // Turns each DetectedTopic's CandidateFields into CandidateRelation
    // rows: guesses which other detected Topic a field like
    // "A_specification_sourceID" points at (by fuzzy name matching), what
    // multiplicity the field's own shape implies, and whether the pointed-
    // at class has a field pointing back (making the relation
    // bidirectional). None of this is meant to be authoritative - it
    // exists to pre-fill the review UI, and every guess is expected to be
    // checked or corrected there, especially target class ties and the
    // Nullable/NilIdentifier presence convention (indistinguishable from
    // syntax alone: both are just a scalar composite-identifier field).
    public static class RelationInference
    {
        private const double AutoPairConfidenceThreshold = 0.5;
        private const double UnresolvedThreshold = 0.3;

        public static List<CandidateRelation> Infer(IReadOnlyList<DetectedTopic> topics)
        {
            var byNormalizedName = topics
                .Select(t => (Topic: t, Key: Normalize(StripClassPrefix(t.ClassName))))
                .ToList();

            var raw = new List<CandidateRelation>();
            foreach (var topic in topics)
            {
                foreach (var field in topic.CandidateFields)
                {
                    var middle = ExtractMiddle(field.FieldName);
                    var (bestTopic, confidence) = FindBestMatch(middle, byNormalizedName, topic);

                    raw.Add(new CandidateRelation
                    {
                        FromClass = topic.ClassName,
                        FromField = field.FieldName,
                        ToClass = confidence >= UnresolvedThreshold ? bestTopic?.ClassName : null,
                        Multiplicity = GuessMultiplicity(field.Shape),
                        PresenceCheck = "Nullable",
                        Confidence = confidence,
                    });
                }
            }

            return PairBidirectional(raw);
        }

        private static string ExtractMiddle(string fieldName)
        {
            var m = Regex.Match(fieldName, @"^A_(.+)_sourceID$");
            return m.Success ? m.Groups[1].Value : fieldName;
        }

        private static string StripClassPrefix(string className) =>
            className.StartsWith("C_", StringComparison.Ordinal) ? className.Substring(2) : className;

        private static string Normalize(string s) =>
            Regex.Replace(s, @"[^A-Za-z0-9]", "").ToLowerInvariant();

        private static (DetectedTopic? Topic, double Confidence) FindBestMatch(
            string middle, List<(DetectedTopic Topic, string Key)> candidates, DetectedTopic exclude)
        {
            var target = Normalize(middle);
            var fromKey = Normalize(StripClassPrefix(exclude.ClassName));
            DetectedTopic? best = null;
            double bestScore = 0;

            foreach (var (topic, key) in candidates)
            {
                double score;
                if (key == target) score = 1.0;
                else if (key.Contains(target) || target.Contains(key)) score = 0.8;
                else score = LongestCommonSubstringRatio(key, target);

                // Tie/near-tie breaker: when a field's middle segment alone
                // (e.g. "specification") matches multiple classes equally
                // well (e.g. both C_Rotational_Mount_Specification and
                // C_Linear_Mount_Specification "contain" it), prefer
                // whichever candidate's name shares more of a prefix with
                // the FROM class's own name - a Linear_Mount field should
                // resolve to the Linear-qualified class, not the
                // Rotational one it happens to sort before. Weighted low
                // enough to only break ties within a tier, never to beat a
                // clearly stronger match in a different tier.
                score += 0.15 * PrefixSimilarity(fromKey, key);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = topic;
                }
            }

            return (best, Math.Min(bestScore, 1.0));
        }

        private static double PrefixSimilarity(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i == 0 ? 0 : (double)i / Math.Max(a.Length, b.Length);
        }

        private static double LongestCommonSubstringRatio(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            var dp = new int[a.Length + 1, b.Length + 1];
            int best = 0;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    if (a[i - 1] == b[j - 1])
                    {
                        dp[i, j] = dp[i - 1, j - 1] + 1;
                        if (dp[i, j] > best) best = dp[i, j];
                    }
            return (double)best / Math.Max(a.Length, b.Length);
        }

        private static string GuessMultiplicity(FieldShape shape) => shape switch
        {
            FieldShape.Enumerable => "ZeroOrMany",
            FieldShape.Nullable => "ZeroOrOne",
            _ => "One",
        };

        // Looks for A->B and B->A candidates guessing at each other and
        // merges each such pair into one bidirectional entry, using each
        // side's own field-shape-derived multiplicity. A field with no
        // single strong reciprocal candidate (zero, or more than one above
        // the threshold) is left unidirectional rather than guessed at -
        // wrong auto-pairing is worse than asking the user to do it by hand.
        private static List<CandidateRelation> PairBidirectional(List<CandidateRelation> raw)
        {
            var consumed = new HashSet<CandidateRelation>();
            var result = new List<CandidateRelation>();

            foreach (var r in raw)
            {
                if (consumed.Contains(r)) continue;
                if (r.ToClass == null) { result.Add(r); continue; }

                var reciprocalCandidates = raw
                    .Where(other => !consumed.Contains(other) && other != r
                                    && other.FromClass == r.ToClass
                                    && other.ToClass == r.FromClass
                                    && other.Confidence >= AutoPairConfidenceThreshold)
                    .ToList();

                if (reciprocalCandidates.Count == 1)
                {
                    var recip = reciprocalCandidates[0];
                    r.Bidirectional = true;
                    r.ReciprocalField = recip.FromField;
                    r.ReciprocalMultiplicity = recip.Multiplicity;
                    consumed.Add(recip);
                }

                result.Add(r);
            }

            return result;
        }
    }
}
