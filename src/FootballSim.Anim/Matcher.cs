namespace FootballSim.Anim;

/// <summary>
/// Motion matching inside one chooser set: a brute-force search for the searchable frame whose normalised features are nearest the
/// query (squared distance). Ties go to the lower frame index, so a replay always picks the same frames. A save set searches only the
/// clips moving to the side of the save (by the save point relative to his facing, never by world axes; mirroring gives both).
/// </summary>
public static class Matcher
{
    /// <summary>How much better (as a fraction of its cost) the best frame must be before it replaces the current one.</summary>
    public const float KeepMargin = 0.05f;
    /// <summary>Leaving the end of a clip, the search skips the last this many seconds before it in the same clip.</summary>
    public const float LoopBackSeconds = 0.5f;

    /// <summary>The nearest searchable frame to the normalised query, or -1 when the set has none.</summary>
    public static int Best(PoseSet set, ReadOnlySpan<float> query, out float cost) => Best(set, query, out cost, 0, 0);

    /// <summary>
    /// <see cref="Best(PoseSet, ReadOnlySpan{float}, out float)"/> leaving out the frames <paramref name="skipFrom"/> .. <paramref name="skipTo"/> − 1,
    /// and, with <paramref name="side"/> −1 (his left) or +1 (his right), the frames of clips moving to the other side (clips moving to
    /// neither side always count).
    /// </summary>
    public static int Best(PoseSet set, ReadOnlySpan<float> query, out float cost, int skipFrom, int skipTo, int side = 0)
    {
        int best = -1;
        cost = float.PositiveInfinity;
        for (int i = 0; i < set.FrameCount; i++)
        {
            if (!set.Searchable(i) || (i >= skipFrom && i < skipTo) || side * set.Side(i) < 0) continue;
            var f = set.Normalized(i);
            float bias = set.Bias(i), limit = cost / bias;
            float d = 0;
            for (int k = 0; k < f.Length; k++)
            {
                float e = f[k] - query[k];
                d += e * e;
                if (d >= limit) break;   // cannot win (equal does not win either: ties keep the lower index)
            }
            d *= bias;
            if (d < cost) { cost = d; best = i; }
        }
        return best;
    }

    /// <summary>The squared distance of one frame's features from the query, times its clip's bias.</summary>
    public static float Cost(PoseSet set, int frame, ReadOnlySpan<float> query)
    {
        var f = set.Normalized(frame);
        float d = 0;
        for (int k = 0; k < f.Length; k++) { float e = f[k] - query[k]; d += e * e; }
        return d * set.Bias(frame);
    }

    /// <summary>
    /// The frame to play: the best frame, unless <paramref name="current"/> (a frame of this set, or -1) is still searchable and the
    /// best does not beat its cost by <see cref="KeepMargin"/>, or the best lies in the same clip within <paramref name="nearSeconds"/>
    /// of it. A current frame past its clip's searchable end must change, and not to the last <see cref="LoopBackSeconds"/> of the
    /// same clip. Returns -1 only when the set has no searchable frame. <paramref name="keepMargin"/> replaces
    /// <see cref="KeepMargin"/> (the attacker's locomotion asks for more: its recordings are short and a steady run should play them through).
    /// </summary>
    public static int Search(PoseSet set, ReadOnlySpan<float> query, int current, float nearSeconds = 0f, int side = 0, float keepMargin = KeepMargin)
    {
        // run off the end of its clip: never straight back into the stretch just played (it would loop a few frames)
        int skipFrom = 0, skipTo = 0;
        if (current >= 0 && current < set.FrameCount && !set.Searchable(current))
        {
            var pc = set.Locate(current, out _);
            skipFrom = Math.Max(pc.Start, current - (int)MathF.Round(LoopBackSeconds * pc.Clip.Fps)); skipTo = pc.End;
        }
        int best = Best(set, query, out float bestCost, skipFrom, skipTo, side);
        if (best < 0) best = Best(set, query, out bestCost, 0, 0, side);
        if (best < 0) best = Best(set, query, out bestCost);   // nothing on that side: any frame rather than none
        if (best < 0) return -1;
        if (current >= 0 && current < set.FrameCount && current != best && set.Searchable(current) && side * set.Side(current) >= 0)
        {
            // a best frame in the playing clip close to the current one is the motion already playing: no jump
            var pc = set.Locate(current, out _);
            int near = (int)MathF.Round(nearSeconds * pc.Clip.Fps);
            if (best >= pc.Start && best < pc.End && Math.Abs(best - current) <= near) return current;
            float cur = Cost(set, current, query);
            if (bestCost >= (1f - keepMargin) * cur) return current;
        }
        return best;
    }
}
