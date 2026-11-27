using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace SolidWorksAssetExporter.AddIn
{
    internal sealed class SwBindingViewport : IDisposable
    {
        private static readonly Outline[] NoOutlines = new Outline[0];
        private readonly ModelDoc2 _document;
        private ModelView _view;
        private volatile Outline[] _outlines = NoOutlines;
        private bool _disposed;

        internal SwBindingViewport(ModelDoc2 document)
        {
            _document = document;
        }

        internal int Show(IEnumerable<KeyValuePair<Component2, Color>> components)
        {
            if (_disposed) return 0;
            var outlines = new List<Outline>();
            foreach (var component in components)
            {
                if (component.Key == null) continue;
                double[] bounds;
                try { bounds = component.Key.GetBox(false, false) as double[]; }
                catch (COMException) { continue; }
                catch (InvalidComObjectException) { continue; }
                if (bounds == null || bounds.Length != 6 || bounds.Any(value => double.IsNaN(value) || double.IsInfinity(value))) continue;
                outlines.Add(new Outline(bounds, component.Value));
            }

            if (outlines.Count == 0)
            {
                Clear();
                return 0;
            }
            try
            {
                var view = _document.IActiveView;
                if (view == null) { Clear(); return 0; }
                if (!Equals(_view, view))
                {
                    Clear();
                    _view = view;
                    _view.DestroyNotify += OnViewDestroyed;
                    _view.BufferSwapNotify += Draw;
                }
                else if (Matches(outlines)) return outlines.Count;

                _outlines = outlines.ToArray();
                view.GraphicsRedraw(null);
                return outlines.Count;
            }
            catch (COMException) { Clear(); return 0; }
            catch (InvalidComObjectException) { Clear(); return 0; }
        }

        internal void Clear()
        {
            var active = _outlines.Length != 0;
            _outlines = NoOutlines;
            var view = _view;
            _view = null;
            if (view == null) return;
            Detach(view);
            if (!active) return;
            try { view.GraphicsRedraw(null); }
            catch (COMException) { }
            catch (InvalidComObjectException) { }
        }

        public void Dispose()
        {
            _disposed = true;
            Clear();
        }

        private int OnViewDestroyed()
        {
            _outlines = NoOutlines;
            var view = _view;
            _view = null;
            if (view != null) Detach(view);
            return 0;
        }

        private void Detach(ModelView view)
        {
            try { view.BufferSwapNotify -= Draw; }
            catch (COMException) { }
            catch (InvalidComObjectException) { }
            try { view.DestroyNotify -= OnViewDestroyed; }
            catch (COMException) { }
            catch (InvalidComObjectException) { }
        }

        private bool Matches(List<Outline> outlines)
        {
            var previous = _outlines;
            if (previous.Length != outlines.Count) return false;
            for (var index = 0; index < previous.Length; index++)
                if (previous[index].Color != outlines[index].Color || !previous[index].Bounds.SequenceEqual(outlines[index].Bounds))
                    return false;
            return true;
        }

        private int Draw()
        {
            var outlines = _outlines;
            if (outlines.Length == 0 || wglGetCurrentContext() == IntPtr.Zero) return 0;

            int depth, capacity;
            glGetIntegerv(GlAttribStackDepth, out depth);
            glGetIntegerv(GlMaxAttribStackDepth, out capacity);
            if (depth >= capacity) return 0;

            // SOLIDWORKS supplies assembly modelview and projection matrices in BufferSwapNotify.
            // Leave those matrices in place and restore the previous shader and drawing state.
            int program = 0;
            var address = wglGetProcAddress("glUseProgram");
            var validAddress = address.ToInt64() > 3 || address.ToInt64() < -1;
            UseProgram useProgram = null;
            if (validAddress)
            {
                glGetIntegerv(GlCurrentProgram, out program);
                if (program != 0) useProgram = (UseProgram)Marshal.GetDelegateForFunctionPointer(address, typeof(UseProgram));
            }

            glPushAttrib(GlCurrentBit | GlLineBit | GlDepthBufferBit | GlEnableBit | GlColorBufferBit);
            try
            {
                if (useProgram != null) useProgram(0);
                glDisable(GlLighting);
                glDisable(GlTexture1D);
                glDisable(GlTexture2D);
                glDisable(GlBlend);
                glDisable(GlAlphaTest);
                glDisable(GlFog);
                glDisable(GlLineStipple);
                glDisable(GlDepthTest);
                glColorMask(1, 1, 1, 1);
                glLineWidth(3f);
                glBegin(GlLines);
                try
                {
                    foreach (var outline in outlines)
                    {
                        glColor3ub(outline.Color.R, outline.Color.G, outline.Color.B);
                        DrawEdges(outline.Bounds);
                    }
                }
                finally { glEnd(); }
            }
            finally
            {
                if (useProgram != null) useProgram((uint)program);
                glPopAttrib();
            }
            return 0;
        }

        private static void DrawEdges(double[] bounds)
        {
            for (var corner = 0; corner < 8; corner++)
            {
                for (var axis = 0; axis < 3; axis++)
                {
                    var bit = 1 << axis;
                    if ((corner & bit) != 0) continue;
                    DrawVertex(bounds, corner);
                    DrawVertex(bounds, corner | bit);
                }
            }
        }

        private static void DrawVertex(double[] bounds, int corner)
        {
            glVertex3d(bounds[(corner & 1) == 0 ? 0 : 3],
                bounds[(corner & 2) == 0 ? 1 : 4], bounds[(corner & 4) == 0 ? 2 : 5]);
        }

        private sealed class Outline
        {
            internal readonly double[] Bounds;
            internal readonly Color Color;

            internal Outline(double[] bounds, Color color)
            {
                Bounds = bounds;
                Color = color;
            }
        }

        private const uint GlLines = 0x0001;
        private const uint GlCurrentBit = 0x0001;
        private const uint GlLineBit = 0x0004;
        private const uint GlDepthBufferBit = 0x0100;
        private const uint GlEnableBit = 0x2000;
        private const uint GlColorBufferBit = 0x4000;
        private const uint GlLineStipple = 0x0B24;
        private const uint GlLighting = 0x0B50;
        private const uint GlFog = 0x0B60;
        private const uint GlDepthTest = 0x0B71;
        private const uint GlAttribStackDepth = 0x0BB0;
        private const uint GlAlphaTest = 0x0BC0;
        private const uint GlBlend = 0x0BE2;
        private const uint GlMaxAttribStackDepth = 0x0D35;
        private const uint GlTexture1D = 0x0DE0;
        private const uint GlTexture2D = 0x0DE1;
        private const uint GlCurrentProgram = 0x8B8D;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void UseProgram(uint program);

        [DllImport("opengl32.dll")] private static extern IntPtr wglGetCurrentContext();
        [DllImport("opengl32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr wglGetProcAddress(string name);
        [DllImport("opengl32.dll")] private static extern void glGetIntegerv(uint name, out int value);
        [DllImport("opengl32.dll")] private static extern void glPushAttrib(uint mask);
        [DllImport("opengl32.dll")] private static extern void glPopAttrib();
        [DllImport("opengl32.dll")] private static extern void glDisable(uint capability);
        [DllImport("opengl32.dll")] private static extern void glColorMask(byte red, byte green, byte blue, byte alpha);
        [DllImport("opengl32.dll")] private static extern void glLineWidth(float width);
        [DllImport("opengl32.dll")] private static extern void glBegin(uint mode);
        [DllImport("opengl32.dll")] private static extern void glEnd();
        [DllImport("opengl32.dll")] private static extern void glColor3ub(byte red, byte green, byte blue);
        [DllImport("opengl32.dll")] private static extern void glVertex3d(double x, double y, double z);
    }
}
