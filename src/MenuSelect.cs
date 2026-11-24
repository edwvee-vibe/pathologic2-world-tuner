using System;
using UnityEngine;

namespace Pathologic2WorldTuner
{
    /// <summary>
    /// A drop-down like an HTML select: a box with the current value and a triangle; a click opens the list over the window
    /// content (it does not move the layout), a click on an item picks it, a click anywhere else, the wheel or Esc closes it.
    /// IMGUI has no pop-ups, so the list is drawn at the end of the window and its clicks are taken at the start of the window,
    /// before the controls under it can see them.
    /// </summary>
    internal static partial class Menu
    {
        private const float SelectItemHeight = 24f;

        private static string selectOpenId;       // which select is open (null = none)
        private static Vector2 selectScreenPos;    // bottom-left of the box, in screen space
        private static float selectWidth;
        private static string[] selectItems;
        private static int selectCurrent;
        private static Action<int> selectPick;
        private static GUIStyle selectBoxStyle, selectItemStyle, selectItemOnStyle;
        private static Texture2D selectListBackground, selectHoverBackground;

        internal static bool SelectOpen => selectOpenId != null;

        private static int selectClosedFrame = -1;

        internal static void CloseSelect()
        {
            if (selectOpenId != null)
            {
                selectClosedFrame = Time.frameCount;
            }
            selectOpenId = null;
            selectItems = null;
            selectPick = null;
        }

        /// <summary>The closed box; <paramref name="pick"/> gets the index of the chosen item.</summary>
        private static void Select(string id, string shown, string[] items, int current, Action<int> pick, float width)
        {
            EnsureSelectStyles();
            bool open = selectOpenId == id;
            if (GUILayout.Button(shown + (open ? "   ▴" : "   ▾"), selectBoxStyle, GUILayout.Width(width), GUILayout.Height(24)))
            {
                if (open)
                {
                    CloseSelect();
                }
                else
                {
                    selectOpenId = id;
                    open = true;
                }
            }
            if (open)
            {
                // Remember where the list goes (the scroll view and the window move it) and what it shows.
                Rect r = GUILayoutUtility.GetLastRect();
                if (Event.current.type == EventType.Repaint)
                {
                    selectScreenPos = GUIUtility.GUIToScreenPoint(new Vector2(r.x, r.yMax));
                }
                selectWidth = width;
                selectItems = items;
                selectCurrent = current;
                selectPick = pick;
            }
        }

        private static Rect SelectListRect()
        {
            Vector2 p = GUIUtility.ScreenToGUIPoint(selectScreenPos);
            return new Rect(p.x, p.y + 2f, selectWidth, (selectItems?.Length ?? 0) * SelectItemHeight + 4f);
        }

        /// <summary>At the start of the window: clicks and the wheel while the list is open belong to the list.</summary>
        private static void SelectInput()
        {
            if (selectOpenId == null || selectItems == null)
            {
                return;
            }
            Event e = Event.current;
            if (e.type == EventType.ScrollWheel)
            {
                CloseSelect();
                e.Use();
                return;
            }
            if (e.type != EventType.MouseDown)
            {
                return;
            }
            Rect list = SelectListRect();
            if (list.Contains(e.mousePosition))
            {
                int index = (int)((e.mousePosition.y - list.y - 2f) / SelectItemHeight);
                Action<int> pick = selectPick;
                CloseSelect();
                if (index >= 0 && pick != null)
                {
                    pick(index);
                }
                e.Use();
            }
            else
            {
                // A click on the box itself is left to the box, which closes the list.
                Vector2 box = GUIUtility.ScreenToGUIPoint(selectScreenPos);
                var boxRect = new Rect(box.x, box.y - 26f, selectWidth, 26f);
                if (!boxRect.Contains(e.mousePosition))
                {
                    CloseSelect();
                    e.Use();
                }
            }
        }

        /// <summary>At the end of the window: the list over everything else.</summary>
        private static void SelectDraw()
        {
            if (selectOpenId == null || selectItems == null || Event.current.type != EventType.Repaint)
            {
                return;
            }
            Rect list = SelectListRect();
            GUI.DrawTexture(list, selectListBackground);
            Vector2 mouse = Event.current.mousePosition;
            for (int i = 0; i < selectItems.Length; i++)
            {
                var r = new Rect(list.x + 2f, list.y + 2f + i * SelectItemHeight, list.width - 4f, SelectItemHeight);
                if (i == selectCurrent)
                {
                    GUI.DrawTexture(r, selectedBackground);
                }
                else if (r.Contains(mouse))
                {
                    GUI.DrawTexture(r, selectHoverBackground);
                }
                (i == selectCurrent ? selectItemOnStyle : selectItemStyle).Draw(r, selectItems[i], false, false, false, false);
            }
        }

        private static void EnsureSelectStyles()
        {
            if (selectBoxStyle != null)
            {
                return;
            }
            selectListBackground = MakeTexture(new Color(0.16f, 0.16f, 0.18f, 0.98f));
            selectHoverBackground = MakeTexture(new Color(0.30f, 0.30f, 0.34f, 1f));
            selectBoxStyle = new GUIStyle(GUI.skin.textField) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 3, 3) };
            selectItemStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 8, 2, 2) };
            selectItemStyle.normal.textColor = new Color(0.88f, 0.88f, 0.88f);
            selectItemOnStyle = new GUIStyle(selectItemStyle) { fontStyle = FontStyle.Bold };
            selectItemOnStyle.normal.textColor = Color.white;
        }
    }
}
