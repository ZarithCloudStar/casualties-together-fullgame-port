// Geometry/style doubles only. These tests cannot verify native Unity glyph
// rasterization; no game or Unity native runtime is started by the test program.
using System;
namespace UnityEngine {
    internal enum FontStyle { Normal }
    internal enum TextAnchor { UpperLeft, MiddleCenter }
    internal enum TextClipping { Clip, Overflow }
    internal class Font { }
    internal class Texture2D { internal static Texture2D whiteTexture = new(); }
    internal struct Color {
        internal float r,g,b,a;
        internal Color(float r,float g,float b,float a=1) { this.r=r;this.g=g;this.b=b;this.a=a; }
        internal static Color white => new(1,1,1);
    }
    internal struct Vector2 {
        internal float x,y;
        internal Vector2(float x,float y) {this.x=x;this.y=y;}
        internal static Vector2 zero => new();
    }
    internal struct Rect {
        internal float x,y,width,height;
        internal Rect(float x,float y,float width,float height) { this.x=x;this.y=y;this.width=width;this.height=height; }
        internal float xMax => x+width;
        internal float yMax => y+height;
        internal Vector2 center {get => new(x+width/2,y+height/2); set {x=value.x-width/2;y=value.y-height/2;}}
        internal bool Contains(Vector2 p)=>p.x>=x&&p.x<xMax&&p.y>=y&&p.y<yMax;
    }
    internal static class Mathf {
        internal static float Max(float x,float y)=>Math.Max(x,y);
        internal static int Max(int x,int y)=>Math.Max(x,y);
        internal static float Min(params float[] v)=>System.Linq.Enumerable.Min(v);
        internal static int RoundToInt(float n)=>(int)Math.Round(n);
        internal static float Ceil(float n)=>(float)Math.Ceiling(n);
    }
    internal class GUIContent {
        internal string text;
        internal GUIContent(string text){this.text=text;}
        internal static GUIContent none=new("");
    }
    internal class GUIStyleState {internal Color textColor;}
    internal class GUIStyle {
        internal Font font;
        internal int fontSize;
        internal FontStyle fontStyle;
        internal TextAnchor alignment;
        internal bool wordWrap,richText;
        internal float fixedHeight,fixedWidth;
        internal Vector2 contentOffset;
        internal TextClipping clipping;
        internal GUIStyleState normal=new();
        internal float lineHeight=>fontSize*1.5f;
        internal float CalcHeight(GUIContent content,float width) {
            float lines=0;
            foreach(string line in content.text.Split('\n')) lines+=(float)Math.Max(1,Math.Ceiling(line.Length*fontSize/Math.Max(1,width)));
            return lineHeight*lines;
        }
        internal static GUIStyle none=new();
    }
    internal class GUISkin {internal GUIStyle label=new(),button=new();internal Font font=new();}
    internal static class GUI {
        internal static bool enabled=true, NextClick;
        internal static Color color=Color.white;
        internal static GUISkin skin=new();
        internal static GUIStyle LastLabel;
        internal static void DrawTexture(Rect rect,Texture2D texture){}
        internal static bool Button(Rect rect,GUIContent content,GUIStyle style)=>enabled&&NextClick;
        internal static void Label(Rect rect,string text,GUIStyle style){LastLabel=style;}
    }
    internal static class Screen {internal static int width=1920,height=1080;}
    internal class Event {internal static Event current=new();internal Vector2 mousePosition=Vector2.zero;}
}
namespace Together {
    internal static class UIBullshit {
        internal static float uiScale=1;
        internal static bool USE_GAME_FONT=true;
        internal static int GetCurFontSize()=>UnityEngine.Mathf.RoundToInt(20*uiScale);
        internal static void CheckCursorOverlap(UnityEngine.Rect rect){}
    }
    internal static class CoopModAssets {internal static UnityEngine.Font gamefont=new();}
}
