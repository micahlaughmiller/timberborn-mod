using UnityEngine;

namespace TimberbornAI
{
    /// <summary>
    /// On-screen caption showing the agent's current goal and last reasoning.
    /// This is the piece that makes the run watchable on video: viewers see
    /// why the AI did something, not just the result.
    /// </summary>
    internal static class OverlayPanel
    {
        private static string _goal = "waiting for agent...";
        private static string _thought = "";
        private static string _lastAction = "";
        private static GUIStyle _goalStyle, _bodyStyle;

        public static string SetNarration(string body)
        {
            var goal = Json.Field(body, "goal");
            var thought = Json.Field(body, "thought");
            var action = Json.Field(body, "action_summary");

            if (goal != null) _goal = goal;
            if (thought != null) _thought = thought;
            if (action != null) _lastAction = action;

            return "{\"ok\":true}";
        }

        public static void Draw()
        {
            if (_goalStyle == null)
            {
                _goalStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 20,
                    fontStyle = FontStyle.Bold,
                    wordWrap = true,
                };
                _goalStyle.normal.textColor = new Color(0.55f, 0.9f, 1f);

                _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
                _bodyStyle.normal.textColor = Color.white;
            }

            const float w = 460f;
            var rect = new Rect(16f, 16f, w, 190f);

            var prior = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = prior;

            GUILayout.BeginArea(new Rect(rect.x + 12f, rect.y + 10f, w - 24f, rect.height - 20f));
            GUILayout.Label("GOAL: " + _goal, _goalStyle);
            GUILayout.Space(6f);
            if (!string.IsNullOrEmpty(_thought))    GUILayout.Label(_thought, _bodyStyle);
            if (!string.IsNullOrEmpty(_lastAction)) GUILayout.Label("> " + _lastAction, _bodyStyle);
            GUILayout.EndArea();
        }
    }
}
