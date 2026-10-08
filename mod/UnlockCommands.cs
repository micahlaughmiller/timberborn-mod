using System;

namespace TimberbornAI
{
    /// <summary>
    /// Spends science points to unlock a building, as the player does in the building panel. Most of what
    /// comes after the basics (planks, stairs, tanks, power, decorations) is locked behind science, and the
    /// Inventor only produces the points; this is the other half.
    /// </summary>
    internal static class UnlockCommands
    {
        public static string Unlock(string body)
        {
            var name = Json.Field(body, "prefab") ?? Json.Field(body, "name");
            if (string.IsNullOrEmpty(name)) return Placer.Fail("unlock requires \"prefab\" (see get_buildings with include_locked)");

            var build = AIBuildServices.Instance;
            if (build == null) return Placer.Fail("no save loaded, or build services not bound");

            object spec;
            try
            {
                if (!GameAccess.Invoke(build.Buildings, "GetBuildingTemplate", out spec, name) || spec == null)
                    return Placer.Fail("unknown building \"" + name + "\"");
            }
            catch (Exception e) { return Placer.Fail("unknown building \"" + name + "\": " + Placer.Root(e).Message); }

            int cost = GameAccess.IntOf(GameAccess.Member(spec, "ScienceCost"));
            int points = build.Science.SciencePoints;

            GameAccess.Invoke(build.Unlocking, "Unlocked", out var already, spec);
            if (already is bool done && done) return Placer.Ok(name + " is already unlocked");

            GameAccess.Invoke(build.Unlocking, "Unlockable", out var can, spec);
            if (!(can is bool ok && ok))
                return Placer.Fail("cannot unlock " + name + " yet: it costs " + cost + " science and you have " + points
                                   + (points >= cost ? ", so a prerequisite building must be unlocked first" : ". Build an Inventor and let it produce science")
                                   + ".");

            try
            {
                if (!GameAccess.Invoke(build.Unlocking, "Unlock", out _, spec))
                    return Placer.Fail("the game has no Unlock for this building; the game version may have changed");
            }
            catch (Exception e)
            {
                var root = Placer.Root(e);
                return Placer.Fail("the game refused to unlock " + name + ": " + root.GetType().Name + ": " + root.Message);
            }

            return Placer.Ok("unlocked " + name + " for " + cost + " science; " + build.Science.SciencePoints + " science left");
        }
    }
}
