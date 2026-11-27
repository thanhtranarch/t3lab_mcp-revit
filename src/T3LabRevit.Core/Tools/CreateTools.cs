using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using static T3LabRevit.Core.Sch;

namespace T3LabRevit.Core
{
    internal static class CreateTools
    {
        public static void Register()
        {
            Registry.Add("create_walls", "Create walls", ToolKind.Write,
                "Create walls from lines. walls=[{start, end, type?, level?, height?, base_offset?, structural?, flip?}] (mm), or " +
                "points=[[x,y],...] for a chain (closed=true joins the last point to the first) with shared type / level / height. " +
                "Defaults: first wall type, the active view's level, height 3000 mm.",
                Obj(P("walls", "{'type':'array','items':{'type':'object'},'description':'Walls: start, end [x,y] mm; optional type, level, height, base_offset, structural, flip'}"),
                    P("points", Pts("Chain of points [x, y] mm")),
                    P("closed", Bool("Close the chain")),
                    P("type", Str("Wall type label or name")),
                    P("level", Str("Base level name or id")),
                    P("height", Num("Unconnected height in mm (default 3000)")),
                    P("top_level", Str("Top constraint level (instead of height)")),
                    P("base_offset", Num("Base offset in mm")),
                    P("structural", Bool("Structural wall"))),
                Walls);

            Registry.Add("create_floor", "Create a floor or ceiling", ToolKind.Write,
                "Create a floor (kind=floor, default) or ceiling (kind=ceiling) from a closed boundary of points [[x,y],...] mm on a " +
                "level, with an optional type and height offset (mm). room_id uses that room's boundary instead.",
                Obj(P("kind", Enum("floor or ceiling", "floor", "ceiling")),
                    P("points", Pts("Boundary points [x, y] mm, in order (closed automatically)")),
                    P("room_id", Int("Use the outer boundary of this room")),
                    P("type", Str("Floor / ceiling type label or name")),
                    P("level", Str("Level name or id")),
                    P("offset", Num("Height offset from the level in mm"))),
                FloorOrCeiling);

            Registry.Add("create_levels", "Create levels", ToolKind.Write,
                "Create levels: levels=[{name, elevation}] (elevation mm). plan=true (default) also makes a floor plan for each.",
                ObjR("levels", P("levels", "{'type':'array','items':{'type':'object','properties':{'name':{'type':'string'},'elevation':{'type':'number'}}},'description':'Levels to create'}"),
                    P("plan", Bool("Create a floor plan per level (default true)"))),
                Levels);

            Registry.Add("create_grids", "Create grids", ToolKind.Write,
                "Create straight grids: grids=[{name?, start, end}] (mm). Names are kept when free.",
                ObjR("grids", P("grids", "{'type':'array','items':{'type':'object','properties':{'name':{'type':'string'},'start':{'type':'array'},'end':{'type':'array'}}},'description':'Grids to create'}")),
                Grids);

            Registry.Add("place_family_instance", "Place family instances", ToolKind.Write,
                "Place instances of a family type at points (mm): furniture, equipment, columns, doors and windows. type = 'Family: Type' " +
                "label (list_types). Doors / windows need a host wall: host_id, or the nearest wall is found. rotation in degrees. " +
                "Structural columns are placed level to the level above unless height is given.",
                ObjR("type,points",
                    P("type", Str("Family type label (Family: Type), type name, or id")),
                    P("category", Str("Category to search the type in (optional)")),
                    P("points", Pts("Insertion points [x, y] or [x, y, z] mm")),
                    P("level", Str("Level name or id (default: active view level)")),
                    P("host_id", Int("Host element (wall) for hosted families")),
                    P("rotation", Num("Rotation in degrees about Z")),
                    P("offset", Num("Elevation from level in mm"))),
                PlaceInstances);

            Registry.Add("create_rooms", "Create rooms", ToolKind.Write,
                "Place rooms on a level: points=[[x,y],...] mm (one room per point, inside enclosed boundaries) with optional names / " +
                "numbers, or all=true to fill every enclosed region on the level that has no room yet.",
                Obj(P("level", Str("Level name or id (default: active view level)")),
                    P("points", Pts("Room points [x, y] mm")),
                    P("names", Strs("Room names, by point order")),
                    P("numbers", Strs("Room numbers, by point order")),
                    P("all", Bool("Create rooms in all closed regions of the level"))),
                Rooms);

            Registry.Add("load_family", "Load a family", ToolKind.Write,
                "Load an .rfa family file into the model (overwrite=true replaces an existing one and its parameter values).",
                ObjR("path", P("path", Str("Path of the .rfa")), P("overwrite", Bool("Replace an existing family of that name"))),
                LoadFamily);
        }

        static WallType WallTypeOf(Document doc, string name)
        {
            List<WallType> all = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().ToList();
            if (string.IsNullOrWhiteSpace(name))
                return all.FirstOrDefault(t => t.Kind == WallKind.Basic) ?? all.FirstOrDefault() ?? throw new ToolError("No wall type in the model.");
            return Rv.FindType(all, name) ?? throw new ToolError($"No wall type '{name}'. Available: " + string.Join(" | ", all.Select(Rv.TypeLabel).Take(40)));
        }

        static object Walls(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            var specs = new List<Tuple<XYZ, XYZ, Args>>();
            foreach (Args w in a.Objs("walls")) specs.Add(Tuple.Create(w.ReqPt("start"), w.ReqPt("end"), w));
            if (a.Has("points"))
            {
                List<XYZ> pts = a.Pts("points");
                for (int i = 0; i + 1 < pts.Count; i++) specs.Add(Tuple.Create(pts[i], pts[i + 1], new Args(null)));
                if (a.Bool("closed", false) && pts.Count > 2) specs.Add(Tuple.Create(pts[pts.Count - 1], pts[0], new Args(null)));
            }
            if (specs.Count == 0) throw new ToolError("Give walls [{start, end}] or points [[x,y],...].");
            var created = new List<ElementId>();
            foreach (var s in specs)
            {
                Args w = s.Item3;
                Level lv = Rv.LevelOrDefault(doc, c.ActiveView, w.Str("level") ?? a.Str("level"));
                WallType wt = WallTypeOf(doc, w.Str("type") ?? a.Str("type"));
                XYZ p0 = new XYZ(s.Item1.X, s.Item1.Y, lv.Elevation), p1 = new XYZ(s.Item2.X, s.Item2.Y, lv.Elevation);
                if (p0.DistanceTo(p1) < U.Ft(10)) throw new ToolError("A wall is shorter than 10 mm.");
                double height = U.Ft(w.Num("height", a.Num("height", 3000)));
                double offset = U.Ft(w.Num("base_offset", a.Num("base_offset", 0)));
                Wall wall = Wall.Create(doc, Line.CreateBound(p0, p1), wt.Id, lv.Id, height, offset, w.Bool("flip", false), w.Bool("structural", a.Bool("structural", false)));
                string top = w.Str("top_level") ?? a.Str("top_level");
                if (top != null) wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(Rv.Level(doc, top).Id);
                created.Add(wall.Id);
            }
            return new Dictionary<string, object> { ["created"] = created.Count, ["element_ids"] = Rv.IdList(created) };
        }

        static CurveLoop Loop(List<XYZ> pts, double z)
        {
            if (pts.Count < 3) throw new ToolError("A boundary needs at least 3 points.");
            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ p0 = new XYZ(pts[i].X, pts[i].Y, z), p1 = new XYZ(pts[(i + 1) % pts.Count].X, pts[(i + 1) % pts.Count].Y, z);
                if (p0.DistanceTo(p1) < 1e-6) continue;
                loop.Append(Line.CreateBound(p0, p1));
            }
            return loop;
        }

        static object FloorOrCeiling(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            bool ceiling = string.Equals(a.Str("kind"), "ceiling", StringComparison.OrdinalIgnoreCase);
            Level lv = Rv.LevelOrDefault(doc, c.ActiveView, a.Str("level"));
            CurveLoop loop;
            if (a.Has("room_id"))
            {
                if (!(Rv.Get(doc, a.IdOrNull("room_id")) is Room room)) throw new ToolError("room_id is not a room.");
                var segs = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                if (segs == null || segs.Count == 0) throw new ToolError("This room has no boundary (not placed or not enclosed).");
                loop = new CurveLoop();
                foreach (BoundarySegment s in segs[0])
                {
                    Curve cv = s.GetCurve();
                    XYZ q0 = cv.GetEndPoint(0), q1 = cv.GetEndPoint(1);
                    if (cv is Line) loop.Append(Line.CreateBound(new XYZ(q0.X, q0.Y, lv.Elevation), new XYZ(q1.X, q1.Y, lv.Elevation)));
                    else loop.Append(cv.CreateTransformed(Transform.CreateTranslation(new XYZ(0, 0, lv.Elevation - q0.Z))));
                }
            }
            else loop = Loop(a.Pts("points"), lv.Elevation);
            var loops = new List<CurveLoop> { loop };
            Element made;
            if (ceiling)
            {
                CeilingType t = Rv.TypeOf<CeilingType>(doc, a.Str("type"));
                made = Ceiling.Create(doc, loops, t.Id, lv.Id);
                if (a.Has("offset")) made.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(a.Mm("offset", 0));
            }
            else
            {
                FloorType t = Rv.TypeOf<FloorType>(doc, a.Str("type"), BuiltInCategory.OST_Floors);
                made = Floor.Create(doc, loops, t.Id, lv.Id);
                if (a.Has("offset")) made.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(a.Mm("offset", 0));
            }
            return new Dictionary<string, object> { ["element_ids"] = Rv.IdList(new[] { made.Id }), ["level"] = lv.Name, ["type"] = Rv.TypeElement(made)?.Name };
        }

        static object Levels(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            bool plan = a.Bool("plan", true);
            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
            var made = new List<object>();
            var ids = new List<ElementId>();
            foreach (Args l in a.Objs("levels"))
            {
                Level lv = Level.Create(doc, l.ReqMm("elevation"));
                if (l.Has("name")) lv.Name = l.Str("name");
                ids.Add(lv.Id);
                var d = new Dictionary<string, object> { ["id"] = Id.V(lv.Id), ["name"] = lv.Name, ["elevation_mm"] = U.Mm(lv.Elevation) };
                if (plan && vft != null)
                {
                    ViewPlan vp = ViewPlan.Create(doc, vft.Id, lv.Id);
                    d["plan_view_id"] = Id.V(vp.Id);
                }
                made.Add(d);
            }
            return new Dictionary<string, object> { ["levels"] = made, ["element_ids"] = Rv.IdList(ids) };
        }

        static object Grids(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            var made = new List<object>();
            var ids = new List<ElementId>();
            foreach (Args g in a.Objs("grids"))
            {
                XYZ p0 = g.ReqPt("start"), p1 = g.ReqPt("end");
                Grid grid = Grid.Create(doc, Line.CreateBound(new XYZ(p0.X, p0.Y, 0), new XYZ(p1.X, p1.Y, 0)));
                if (g.Has("name"))
                {
                    try { grid.Name = g.Str("name"); }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { /* name in use: Revit's next name stays */ }
                }
                ids.Add(grid.Id);
                made.Add(new Dictionary<string, object> { ["id"] = Id.V(grid.Id), ["name"] = grid.Name });
            }
            return new Dictionary<string, object> { ["grids"] = made, ["element_ids"] = Rv.IdList(ids) };
        }

        static Wall NearestWall(Document doc, XYZ p, Level lv)
        {
            Wall best = null;
            double bestD = double.MaxValue;
            foreach (Wall w in new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (!(w.Location is LocationCurve lc)) continue;
                if (lv != null && Id.Valid(w.LevelId) && Id.V(w.LevelId) != Id.V(lv.Id)) continue;
                XYZ flat = new XYZ(p.X, p.Y, lc.Curve.GetEndPoint(0).Z);
                double d = lc.Curve.Distance(flat);
                if (d < bestD) { bestD = d; best = w; }
            }
            if (best == null || bestD > U.Ft(1000)) throw new ToolError("No wall within 1000 mm of the point: give host_id.");
            return best;
        }

        static object PlaceInstances(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            BuiltInCategory bic = a.Has("category") ? Rv.Bic(Rv.Category(doc, a.Str("category"))) : BuiltInCategory.INVALID;
            FamilySymbol sym = Rv.TypeOf<FamilySymbol>(doc, a.ReqStr("type"), bic);
            if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
            Level lv = Rv.LevelOrDefault(doc, c.ActiveView, a.Str("level"));
            BuiltInCategory cat = Rv.Bic(sym.Category);
            bool hosted = cat == BuiltInCategory.OST_Doors || cat == BuiltInCategory.OST_Windows;
            bool column = cat == BuiltInCategory.OST_StructuralColumns;
            Element host = a.Has("host_id") ? Rv.Get(doc, a.IdOrNull("host_id")) : null;
            double rot = a.Num("rotation", 0), off = a.Mm("offset", 0);
            var ids = new List<ElementId>();
            foreach (XYZ raw in a.Pts("points"))
            {
                XYZ p = new XYZ(raw.X, raw.Y, lv.Elevation + raw.Z);
                FamilyInstance fi;
                if (hosted || host != null)
                {
                    Element h = host ?? NearestWall(doc, p, lv);
                    if (h is Wall hw && hw.Location is LocationCurve lc)
                    {
                        IntersectionResult pr = lc.Curve.Project(new XYZ(p.X, p.Y, lc.Curve.GetEndPoint(0).Z));
                        if (pr != null) p = new XYZ(pr.XYZPoint.X, pr.XYZPoint.Y, p.Z);
                    }
                    fi = doc.Create.NewFamilyInstance(p, sym, h, lv, StructuralType.NonStructural);
                }
                else if (column)
                {
                    fi = doc.Create.NewFamilyInstance(p, sym, lv, StructuralType.Column);
                }
                else
                {
                    fi = doc.Create.NewFamilyInstance(p, sym, lv, StructuralType.NonStructural);
                }
                if (Math.Abs(off) > 1e-9 && !hosted)
                    (fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM) ?? fi.get_Parameter(BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM))?.Set(off);
                if (Math.Abs(rot) > 1e-9)
                    ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateUnbound(p, XYZ.BasisZ), U.Rad(rot));
                ids.Add(fi.Id);
            }
            return new Dictionary<string, object> { ["created"] = ids.Count, ["type"] = Rv.TypeLabel(sym), ["level"] = lv.Name, ["element_ids"] = Rv.IdList(ids) };
        }

        static object Rooms(ToolContext c, Args a)
        {
            Document doc = c.Doc;
            Level lv = Rv.LevelOrDefault(doc, c.ActiveView, a.Str("level"));
            var ids = new List<ElementId>();
            if (a.Bool("all", false))
            {
                ids.AddRange(doc.Create.NewRooms2(lv));
            }
            else
            {
                List<XYZ> pts = a.Pts("points");
                List<string> names = a.Strs("names"), numbers = a.Strs("numbers");
                for (int i = 0; i < pts.Count; i++)
                {
                    Room r = doc.Create.NewRoom(lv, new UV(pts[i].X, pts[i].Y));
                    if (i < names.Count && !string.IsNullOrEmpty(names[i])) r.Name = names[i];
                    if (i < numbers.Count && !string.IsNullOrEmpty(numbers[i])) r.Number = numbers[i];
                    ids.Add(r.Id);
                }
            }
            doc.Regenerate();
            var rooms = ids.Select(doc.GetElement).OfType<Room>().Select(r => (object)new Dictionary<string, object>
            {
                ["id"] = Id.V(r.Id), ["name"] = r.Name, ["number"] = r.Number, ["area_m2"] = Math.Round(r.Area * 0.09290304, 3),
                ["enclosed"] = r.Area > 0,
            }).ToList();
            return new Dictionary<string, object> { ["created"] = ids.Count, ["level"] = lv.Name, ["rooms"] = rooms, ["element_ids"] = Rv.IdList(ids) };
        }

        sealed class LoadOptions : IFamilyLoadOptions
        {
            readonly bool _overwrite;
            public LoadOptions(bool overwrite) { _overwrite = overwrite; }

            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = _overwrite;
                return _overwrite;
            }
        }

        static object LoadFamily(ToolContext c, Args a)
        {
            string path = a.ReqStr("path");
            if (!System.IO.File.Exists(path)) throw new ToolError("File not found: " + path);
            bool loaded = c.Doc.LoadFamily(path, new LoadOptions(a.Bool("overwrite", false)), out Family fam);
            if (!loaded && fam == null)
                throw new ToolError("Revit did not load it: a family of that name is already in the model (overwrite=true replaces it).");
            var types = fam == null ? new List<object>() : fam.GetFamilySymbolIds().Select(c.Doc.GetElement).OfType<FamilySymbol>()
                .Select(s => (object)new Dictionary<string, object> { ["id"] = Id.V(s.Id), ["label"] = Rv.TypeLabel(s) }).ToList();
            return new Dictionary<string, object> { ["family"] = fam?.Name, ["category"] = fam?.FamilyCategory?.Name, ["types"] = types };
        }
    }
}
