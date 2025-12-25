using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using WpfApp1.Scripts;

namespace WpfApp1.Models
{
    public class TrafficManager
    {
        public Dictionary<int, TrackedObject> TrackedObjects { get; } = new();
        public HashSet<int> CountedIds { get; } = new();
        public int CountL { get; private set; }
        public int CountF { get; private set; }
        public int CountR { get; private set; }

        public void UpdateTracking(List<Detection> dets, double time, LaneAnalyzer analyzer, bool laneOk)
        {
            var used = new HashSet<int>();
            foreach (var t in TrackedObjects.Values.ToList())
            {
                int best = -1; float maxIou = 0.2f;
                for (int i = 0; i < dets.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    float iou = YoloV8Onnx.IoU(t.LastBox, dets[i].Box);
                    if (iou > maxIou) { maxIou = iou; best = i; }
                }
                if (best != -1)
                {
                    int ln = -1;
                    if (laneOk)
                    {
                        var bc = new Point(dets[best].Box.X + dets[best].Box.Width / 2, dets[best].Box.Y + dets[best].Box.Height);
                        ln = analyzer.TryGetLaneNumberForPoint(bc);
                    }
                    dets[best].TrackId = t.Id;
                    t.Update(dets[best].ClassId, dets[best].Box, time, ln);
                    used.Add(best);
                }
                else t.Missed();
            }
            foreach (var d in dets.Where((_, i) => !used.Contains(i)))
            {
                var nt = new TrackedObject(d.ClassId, d.Box, time, d.ClassName);
                d.TrackId = nt.Id; TrackedObjects[nt.Id] = nt;
            }
            var toRemove = TrackedObjects.Where(kv => kv.Value.ShouldBeDeleted).Select(kv => kv.Key).ToList();
            foreach (var k in toRemove) TrackedObjects.Remove(k);
        }

        public void ProcessCounting(int h, double ratio)
        {
            int lineY = (int)(h * ratio);
            foreach (var t in TrackedObjects.Values)
            {
                if (CountedIds.Contains(t.Id)) continue;
                var center = new Point(t.LastBox.X + t.LastBox.Width / 2, t.LastBox.Y + t.LastBox.Height / 2);
                if (center.Y > lineY)
                {
                    CountedIds.Add(t.Id);
                    if (t.Direction == "L") CountL++; else if (t.Direction == "R") CountR++; else CountF++;
                }
            }
        }

        public void Clear() { TrackedObjects.Clear(); CountedIds.Clear(); CountL = CountF = CountR = 0; }
    }
}