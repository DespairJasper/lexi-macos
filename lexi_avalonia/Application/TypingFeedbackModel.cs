namespace Lexi;

public enum TypingLetterTone { Hint, Neutral, Correct, Wrong }

public readonly record struct TypingLetterFeedback(char Character, TypingLetterTone Tone);

/// <summary>Builds visible per-letter feedback without exposing the answer in dictation mode.</summary>
public static class TypingFeedbackModel
{
    public static IReadOnlyList<TypingLetterFeedback> Build(string target, string input, bool hints, TypingOutcome outcome)
    {
        if (outcome == TypingOutcome.Finished || target.Length == 0) return [];

        var letters = new List<TypingLetterFeedback>(hints ? Math.Max(target.Length, input.Length) : input.Length);
        if (!hints)
        {
            for (var i = 0; i < input.Length; i++)
            {
                var correct = i < target.Length && char.ToLowerInvariant(input[i]) == char.ToLowerInvariant(target[i]);
                letters.Add(new TypingLetterFeedback(input[i], correct ? TypingLetterTone.Correct : TypingLetterTone.Wrong));
            }
            return letters;
        }

        for (var i = 0; i < Math.Max(target.Length, input.Length); i++)
        {
            if (i >= input.Length)
            {
                if (i < target.Length) letters.Add(new TypingLetterFeedback(target[i], TypingLetterTone.Hint));
                continue;
            }

            var character = input[i];
            var correct = i < target.Length && char.ToLowerInvariant(character) == char.ToLowerInvariant(target[i]);
            letters.Add(new TypingLetterFeedback(character, correct ? TypingLetterTone.Correct : TypingLetterTone.Wrong));
        }

        return letters;
    }
}
