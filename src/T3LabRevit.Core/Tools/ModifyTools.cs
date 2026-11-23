using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    internal static class ModifyTools
    {
        public static void Register()
        {
            Registry.Add("transform_elements", "Move, copy, rotate or mirror", ToolKind.Write,
                "operation=move (vector [dx,dy,dz] mm), copy (vector; copies=N makes N copies spaced by vector), rotate (angle degrees " +
                "about a vertical axis through center [x,y] mm, default the elements' centre), mirror (axis_start / axis_end [x,y] mm; " +
                "copy=true keeps the originals). One Undo step; new ids are returned for copy / mirror copies.",
                ObjR("element_ids,operation", Ids,
                    P("operation", Enum("What to do", "move", "copy", "rotate", "mirror")),
                    P("vector", Pt("Offset [dx, dy] or [dx, dy, dz] mm")),
                    P("copies", Int("copy: number of copies (default 1)")),
                    P("angle", Num("rotate: degrees, counter-clockwise")),
                    P("center", Pt("rotate: axis point [x, y] mm")),
                    P("axis_start", Pt("mirror: axis start [x, y] mm")),
                    P("axis_end", Pt("mirror: axis end [x, y] mm")),
                    P("copy", Bool("mirror: keep the originals"))),
                Transform);

            Registry.Add("delete_elements", "Delete elements", ToolKind.Write,
                "Delete elements (and what depends on them: tags, hosted doors...). dry_run=true only reports what would be deleted. " +
                "One Undo step.",
                ObjR("element_ids", Ids, P("dry_run", Bool("Report only, delete nothing"))),
                Delete, destructive: true);
        }

        static XYZ Center(Document doc, IEnumerable<ElementId> ids)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, z = 0;
            bool any = false;
            foreach (ElementId id in ids)
            {
                BoundingBoxXYZ b = doc.GetElement(id)?.get_BoundingBox(null);
                if (b == null) continue;
                any = true;
                minX = Math.Min(minX, b.Min.X); minY = Math.Min(minY, b.Min.Y);
                maxX = Math.Max(maxX, b.Max.X); maxY = Math.Max(maxY, b.Max.Y);
                z = b.Min.Z;
            }
            if (!any) throw new ToolError("The elements have no extents: give center.");
            return new XYZ((minX + maxX) / 2, (minY + maxY) / 2, z);
        }

        static object Transform(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            List<ElementId> ids = a.Ids("element_ids");
            if (ids.Count == 0) throw new ToolError("element_ids is empty.");
            Rv.GetMany(doc, ids);
            string op = a.ReqStr("operation").ToLowerInvariant();
            var result = new Dictionary<string, object> { ["operation"] = op, ["elements"] = ids.Count };
            switch (op)
            {
                case "move":
                    ElementTransformUtils.MoveElements(doc, ids, a.ReqPt("vector"));
                    result["element_ids"] = Rv.IdList(ids);
                    break;
                case "copy":
                {
                    XYZ v = a.ReqPt("vector");
                    int n = Math.Max(1, Math.Min(200, a.Int("copies", 1)));
                    var made = new List<ElementId>();
                    for (int i = 1; i <= n; i++) made.AddRange(ElementTransformUtils.CopyElements(doc, ids, v.Multiply(i)));
                    result["element_ids"] = Rv.IdList(made);
                    result["created"] = made.Count;
                    break;
                }
                case "rotate":
                {
                    XYZ center = a.Has("center") ? a.ReqPt("center") : Center(doc, ids);
                    ElementTransformUtils.RotateElements(doc, ids, Line.CreateUnbound(center, XYZ.BasisZ), U.Rad(a.ReqNum("angle")));
                    result["element_ids"] = Rv.IdList(ids);
                    break;
                }
                case "mirror":
                {
                    XYZ p0 = a.ReqPt("axis_start"), p1 = a.ReqPt("axis_end");
                    XYZ dir = new XYZ(p1.X - p0.X, p1.Y - p0.Y, 0);
                    if (dir.GetLength() < 1e-9) throw new ToolError("axis_start and axis_end are the same point.");
                    XYZ normal = dir.Normalize().CrossProduct(XYZ.BasisZ);
                    Plane plane = Plane.CreateByNormalAndOrigin(normal, new XYZ(p0.X, p0.Y, 0));
                    bool keep = a.Bool("copy", false);
                    var before = new HashSet<long>(new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => Id.V(e.Id)));
                    ElementTransformUtils.MirrorElements(doc, ids, plane, keep);
                    if (keep)
                    {
                        doc.Regenerate();
                        var made = new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.Id).Where(i => !before.Contains(Id.V(i))).ToList();
                        result["element_ids"] = Rv.IdList(made);
                        result["created"] = made.Count;
                    }
                    else result["element_ids"] = Rv.IdList(ids);
                    break;
                }
                default: throw new ToolError("Unknown operation " + op);
            }
            return result;
        }

        static object Delete(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            List<ElementId> ids = a.Ids("element_ids");
            if (ids.Count == 0) throw new ToolError("element_ids is empty.");
            List<Element> els = Rv.GetMany(doc, ids);
            var byCategory = els.GroupBy(e => e.Category?.Name ?? "(none)").ToDictionary(g => g.Key, g => (object)g.Count());
            ICollection<ElementId> deleted;
            if (a.Bool("dry_run", false))
            {
                using (var st = new SubTransaction(doc))
                {
                    st.Start();
                    deleted = doc.Delete(ids);
                    st.RollBack();
                }
                return new Dictionary<string, object>
                {
                    ["dry_run"] = true, ["would_delete"] = deleted.Count, ["requested"] = ids.Count, ["by_category"] = byCategory,
                    ["dependents"] = Rv.IdList(deleted.Where(d => !ids.Any(i => Id.V(i) == Id.V(d))).Take(100)),
                };
            }
            deleted = doc.Delete(ids);
            return new Dictionary<string, object> { ["deleted"] = deleted.Count, ["requested"] = ids.Count, ["by_category"] = byCategory };
        }
    }
}
