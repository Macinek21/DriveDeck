using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.UI;

namespace DriveDeck.Effects
{
    /// <summary>
    /// GPU video effect that burns a text stamp (time, GPS speed, position, label) into recorded frames.
    /// The app updates the text through the shared property set ("text"); the effect only draws it.
    /// </summary>
    public sealed class OverlayVideoEffect : IBasicVideoEffect
    {
        private const string TextKey = "text"; // the app sets this key in the shared property set
        private const int MaxTextLength = 160;

        private CanvasDevice _device;
        private IPropertySet _config;
        private CanvasTextFormat _format;
        private float _formatForHeight;
        private CanvasTextLayout _layout;
        private string _layoutText;

        public bool IsReadOnly { get { return false; } }
        public MediaMemoryTypes SupportedMemoryTypes { get { return MediaMemoryTypes.Gpu; } }
        public bool TimeIndependent { get { return true; } }

        public IReadOnlyList<VideoEncodingProperties> SupportedEncodingProperties
        {
            get
            {
                var p = new VideoEncodingProperties();
                p.Subtype = "ARGB32";
                return new List<VideoEncodingProperties> { p };
            }
        }

        public void SetProperties(IPropertySet configuration) { _config = configuration; }

        public void SetEncodingProperties(VideoEncodingProperties encodingProperties, IDirect3DDevice device)
        {
            DropLayout();
            if (_device != null) _device.Dispose();
            _device = CanvasDevice.CreateFromDirect3D11Device(device);
        }

        private string CurrentText()
        {
            object o = null;
            if (_config != null && _config.TryGetValue(TextKey, out o))
            {
                var s = o as string;
                if (s != null) return s.Length > MaxTextLength ? s.Substring(0, MaxTextLength) : s;
            }
            return null;
        }

        public void ProcessFrame(ProcessVideoFrameContext context)
        {
            if (_device == null) return;
            using (var input = CanvasBitmap.CreateFromDirect3D11Surface(_device, context.InputFrame.Direct3DSurface))
            using (var output = CanvasRenderTarget.CreateFromDirect3D11Surface(_device, context.OutputFrame.Direct3DSurface))
            using (var ds = output.CreateDrawingSession())
            {
                // Plain copy: no blending of a whole opaque frame.
                ds.DrawImage(input, 0, 0, input.Bounds, 1, CanvasImageInterpolation.NearestNeighbor, CanvasComposite.Copy);
                string text = CurrentText();
                if (string.IsNullOrEmpty(text)) return;

                float h = (float)output.Size.Height;
                if (_format == null || Math.Abs(_formatForHeight - h) > 1)
                {
                    if (_format != null) _format.Dispose();
                    _format = new CanvasTextFormat
                    {
                        FontFamily = "Segoe UI",
                        FontSize = Math.Max(14f, h / 30f),
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                        WordWrapping = CanvasWordWrapping.NoWrap
                    };
                    _formatForHeight = h;
                    DropLayout();
                }
                if (_layout == null || _layoutText != text) // laid out once per text, not once per frame
                {
                    DropLayout();
                    _layout = new CanvasTextLayout(_device, text, _format, (float)output.Size.Width, h);
                    _layoutText = text;
                }
                {
                    var b = _layout.LayoutBounds;
                    float pad = _format.FontSize * 0.35f;
                    float x = pad * 2, y = h - (float)b.Height - pad * 3;
                    ds.FillRectangle(new Rect(x - pad, y - pad * 0.5, b.Width + pad * 2, b.Height + pad), Color.FromArgb(140, 0, 0, 0));
                    ds.DrawTextLayout(_layout, x, y, Colors.White);
                }
            }
        }

        private void DropLayout()
        {
            if (_layout != null) _layout.Dispose();
            _layout = null;
            _layoutText = null;
        }

        public void Close(MediaEffectClosedReason reason)
        {
            DropLayout();
            if (_format != null) { _format.Dispose(); _format = null; }
            if (_device != null) { _device.Dispose(); _device = null; }
        }

        public void DiscardQueuedFrames() { }
    }
}
