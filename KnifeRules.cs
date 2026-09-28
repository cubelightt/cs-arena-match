// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 cubelightt

namespace ArenaMatch;

public static class KnifeRules
{
    // CS2 timeout reasons: draw, target saved, hostages not rescued, terrorists not escaped.
    public static bool IsTimeout(int reason) => reason is 10 or 12 or 13 or 14;

    public static int ResolveTimeout(IEnumerable<int> ctHealth, IEnumerable<int> tHealth, bool ctWinsTie)
    {
        var ct = ctHealth.Where(hp => hp > 0).ToArray();
        var t = tHealth.Where(hp => hp > 0).ToArray();
        if (ct.Length != t.Length) return ct.Length > t.Length ? 3 : 2;
        if (ct.Sum() != t.Sum()) return ct.Sum() > t.Sum() ? 3 : 2;
        return ctWinsTie ? 3 : 2;
    }
}
