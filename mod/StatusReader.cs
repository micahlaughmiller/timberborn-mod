using System;
using System.Collections.Generic;
using System.Linq;

namespace TimberbornAI
{
    /// <summary>
    /// Reads the warnings the game itself shows for a building ("Unconnected building", "Building
    /// unstaffed", "Construction lacks materials", "No good selected"). They come from the building's
    /// StatusSubject, so the agent sees exactly what a player sees on screen instead of inferring it.
    /// The type is looked up by name and read by reflection, because the status classes are not public.
    /// </summary>
    internal static class StatusReader
    {
        private static Type _subjectType;

        private static Type SubjectType()
        {
            if (_subjectType != null) return _subjectType;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Timberborn.StatusSystem") continue;
                _subjectType = asm.GetType("Timberborn.StatusSystem.StatusSubject");
                break;
            }
            return _subjectType;
        }

        /// <summary>JSON array of the building's active warning texts, "[]" if none, "null" if unreadable.</summary>
        public static string ProblemsJson(object entity)
        {
            try
            {
                var type = SubjectType();
                if (type == null) return "null";

                var subject = Components.Get(entity, type);
                if (subject == null) return "[]";

                var items = new List<string>();
                foreach (var status in GameAccess.Enumerate(GameAccess.Member(subject, "ActiveStatuses")))
                {
                    if (status == null) continue;

                    // The text may sit on the status itself, on its specification, or on its toggle's.
                    var toggle = GameAccess.Member(status, "StatusToggle");
                    var candidates = new[]
                    {
                        status,
                        GameAccess.Member(status, "StatusSpecification"),
                        GameAccess.Member(toggle, "StatusSpecification"),
                        toggle
                    };

                    string text = null;
                    foreach (var candidate in candidates)
                    {
                        if (candidate == null) continue;
                        text = GameAccess.MemberAny(candidate, "Description", "StatusDescription", "Text", "Name") as string;
                        if (!string.IsNullOrEmpty(text)) break;
                    }

                    if (!string.IsNullOrEmpty(text))
                    {
                        items.Add(Json.Str(text));
                    }
                    else
                    {
                        // Unknown shape: show what is there so the real field names can be read off /state.
                        items.Add("{\"unreadable_status\":" + Json.Str(status.GetType().FullName)
                                  + ",\"fields\":" + Describer.Describe(status)
                                  + ",\"spec_fields\":" + Describer.Describe(candidates[1] ?? candidates[2]) + "}");
                    }
                }

                return "[" + string.Join(",", items) + "]";
            }
            catch
            {
                return "null";
            }
        }
    }
}
