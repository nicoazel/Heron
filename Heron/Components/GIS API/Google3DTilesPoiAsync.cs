using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using GrasshopperAsyncComponent.Google;
using Heron.Utilities.Google3DTiles;
using Rhino;
using Rhino.Geometry;
using Rhino.Render.CustomRenderMeshes;
using Rhino.UI.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SysEnv = System.Environment;


namespace Heron.Components.GIS_API
{
    public class Google3DTilesPoiAsync : GH_AsyncComponentGoogle<Google3DTilesPoiAsync>
    {
        public override Guid ComponentGuid => new Guid("93AE1A7A-1FA0-4DA0-B651-FEEA23F5DD16");

        protected override System.Drawing.Bitmap Icon => Properties.Resources.vector;

        public override GH_Exposure Exposure => GH_Exposure.quarternary;

        public Google3DTilesPoiAsync()
          : base("Google 3D Tiles POI Async", "G3DTilesPOIAsync",
              "Asynchronously fetches Points of Interest (POI) from Google 3D Tiles based on specified parameters.",
              "Heron", "GIS API")
        {
            BaseWorker = new Google3DTilesPoiWorker(this);
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager p)
        {

            // Order requested: POIs, Boundary, LOD, MaxSSE, K, Folder, API Key, Download
            string defaultCacheFolder;
            var rhinoDocPath = Rhino.RhinoDoc.ActiveDoc?.Path;
            if (!string.IsNullOrEmpty(rhinoDocPath))
            {
                var docDir = Path.GetDirectoryName(rhinoDocPath);
                defaultCacheFolder = Path.Combine(docDir, "Heron3DTilesCache");
            }
            else
            {
                defaultCacheFolder = Path.Combine(
                    SysEnv.GetFolderPath(SysEnv.SpecialFolder.LocalApplicationData),
                    "Heron3DTilesCache");
            }

            p.AddPointParameter("Point of Interest", "P", "Point of Interest (POI) to center LOD levels around. " +
                "Multiple points can be used to increase detail at multiple locations within the boundary.", GH_ParamAccess.list);
            p.AddCurveParameter("Boundary", "B", "Boundary (planar) in model coordinates (use Heron to place by lat/lon).", GH_ParamAccess.item);
            p.AddIntegerParameter("Level of Detail", "LOD", "Maximum Level of Detail (LOD) closest to the POI. 0 to 20, 20 = max.", GH_ParamAccess.item, 4);
            p.AddIntegerParameter("Max Screen Space Error", "E", "1 to 100, lower values results more detailed meshes closer to the POI.\r\n " +
                "Max Performance = 64\r\n " +
                "Mobile Default = 32\r\n " +
                "Standard = 16\r\n " +
                "High Fidelity = 8\r\n " +
                "Cinematic = 1 to 2", GH_ParamAccess.item, 16);
            p.AddIntegerParameter("Constant (K)", "C", "Constant K for POI influence on LOD (higher = more influence, min 100).  " +
                "K is essentially the conversion factor that defines how many pixels a 1-meter object occupies when it is exactly 1 meter away from the camera.\r\n " +
                "1080p at 60deg FOV ~= 935\r\n " +
                "4k at 60deg FOV ~= 1870\r\n " +
                "720p at 60deg FOV ~= 620\r\n " +
                "This component is not related to the Rhino viewports, so K needs to be set manually.", GH_ParamAccess.item, 600);
            p.AddTextParameter("Cache Folder", "Fp", "Folder path to store tile cache (.glb files).", GH_ParamAccess.item, defaultCacheFolder);
            p.AddTextParameter("API Key", "K", "Google Maps Platform API key. Or set an Environment Variable 'HERONGOOGLEAPIKEY' with your key as the string.", GH_ParamAccess.item, "");
            p.AddBooleanParameter("Run", "R", "If true, run and Download. If false, do nothing.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager p)
        {
            p.AddBoxParameter("Tile Bounding Boxes", "B", "Bounding boxes of the downloaded tiles in model coordinates.", GH_ParamAccess.list);
        }

        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);
            Menu_AppendItem(
                menu,
                "Cancel",
                (s, e) =>
                {
                    RequestCancellation();
                }
            );
        }

        private sealed class Google3DTilesPoiWorker : WorkerInstanceGoogle<Google3DTilesPoiAsync>
        {
            private List<GH_Point> PoisGH { get; set; } = new List<GH_Point>();
            private List<Point3d> Pois { get; set; } = new List<Point3d>();
            private static PointCloud PoiPc { get; set; } = null;
            private Curve Boundary { get; set; } = null;
            private static int MaxLod { get; set; } = 4;
            private static int MaxSse { get; set; } = 16;
            private static int K { get; set; } = 600;
            private string CacheFolder { get; set; } = null;
            private string ApiKey { get; set; } = null;
            private bool Download { get; set; } = false;
            private Polyline Aoi { get; set; } = null;
            private List<Box> TileBoundingBoxes { get; set; } = new List<Box>();

            private static readonly HttpClient client = new HttpClient();

            public Google3DTilesPoiWorker(
                Google3DTilesPoiAsync parent,
                string id = "baseworker",
                CancellationToken cancellationToken = default) : base(parent, id, cancellationToken) { }

            public override Task DoWork(Action<string, double> reportProgress, Action done)
            {
                try 
                {
                    TilesetWalkerAsync(reportProgress);
                    done();
                }
                //catch (Exception ex)
                //{
                //    Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Error in Google 3D Tiles POI Worker: {ex.Message}");
                //    done();
                //    return Task.CompletedTask;
                //}
                catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
                {
                    //No need to call `done()` - GrasshopperAsyncComponent assumes immediate cancel,
                    //thus it has already performed clean-up actions that would normally be done on `done()
                }
                return Task.CompletedTask;
            }

            public async Task TilesetWalkerAsync(Action<string, double> reportProgress)
            {
                // Check for cancellation
                CancellationToken.ThrowIfCancellationRequested();

                // Get Root URL & Session ID (Valid for ~3 hours)
                string rootUrl = $"tile.googleapis.com{ApiKey}";

                var tileset = await client.GetFromJsonAsync<Tileset>(rootUrl);
                if (tileset?.root != null)
                {
                    Console.WriteLine("Starting traversal toward Point of Interest...");
                    await TraverseTileAsync(tileset.root, MaxSse, reportProgress);
                }
            }

            private async Task TraverseTileAsync(Tile tile, double maxSse, Action<string, double> reportProgress)
            {
                // Check for cancellation
                CancellationToken.ThrowIfCancellationRequested();
                int countTiles = 0;

                double distance = CalculateDistanceToPOI(tile);
                double sse = (tile.GeometricError * K) / Math.Max(distance, 1.0); // k ~ 1100 for 1080p [Previous]

                if (sse > maxSse && tile.Children != null)
                {
                    Console.WriteLine($"Refining: SSE {sse:F2} exceeds {maxSse}. Fetching {tile.Children.Count} children.");
                    CancellationToken.ThrowIfCancellationRequested();

                    // Asynchronous parallel fetch of children [Previous]
                    var tasks = tile.Children.Select(child => TraverseTileAsync(child, maxSse, reportProgress));
                    await Task.WhenAll(tasks);
                }
                else
                {
                    Console.WriteLine($"Optimal LOD reached at SSE {sse:F2}. Download geometry: {tile.Content?.Uri}");
                    TileBoundingBoxes.Add(tile.GetEcefBox().Value);
                    reportProgress($"Tiles: {++countTiles}", 0);
                }
            }

            private static double CalculateDistanceToPOI(Tile tile)
            {
                var bboxCenter = tile.GetEcefBox()?.Center;
                if (bboxCenter == null)
                    return double.MaxValue;
                else
                {
                    var cp = PoiPc.ClosestPoint(bboxCenter.Value);
                    var distance = PoiPc[cp].Location.DistanceTo(bboxCenter.Value);
                    return distance;
                }
            }

            private Box GetAoiEcefBox(Point3d aoiEcefCenter, Point3d aoiEcefMin, Point3d aoiEcefMax)
            {
                var latLonHeightCenter = GeoUtils.EcefToWgs84Gdal(aoiEcefCenter);

                // Create plane for box
                var ecefPlaneXAxis = GeoUtils.Wgs84ToEcefGdal(latLonHeightCenter.lonDeg, latLonHeightCenter.latDeg + 1, latLonHeightCenter.h);
                var ecefPlaneYAxis = GeoUtils.Wgs84ToEcefGdal(latLonHeightCenter.lonDeg + 1, latLonHeightCenter.latDeg, latLonHeightCenter.h);
                var ecefPlane = new Plane(aoiEcefCenter, ecefPlaneXAxis, ecefPlaneYAxis);

                var ecefBox = new Box(ecefPlane, new List<Point3d>() {});

                // Inflate increments in model units. 
                // Set Z to 10,000 meters. Mt. Everest is 8,849 meters.
                var activeDoc = RhinoDoc.ActiveDoc;
                if (activeDoc == null)
                    throw new Exception("Active Rhino document required for EarthAnchorPoint conversion.");
                double unitScaleModelToMeters = Rhino.RhinoMath.UnitScale(activeDoc.ModelUnitSystem, UnitSystem.Meters);

                ecefBox.Inflate(0, 0, 10000 / unitScaleModelToMeters);

                return ecefBox;
            }

            public override WorkerInstanceGoogle<Google3DTilesPoiAsync> Duplicate(
                string id, 
                CancellationToken cancellationToken) => new Google3DTilesPoiWorker(Parent, id, cancellationToken);

            public override void GetData(IGH_DataAccess da, GH_ComponentParamServer parameters)
            {                
                var doc = RhinoDoc.ActiveDoc;
                double modelTol = doc != null ? doc.ModelAbsoluteTolerance : 0.01;

                // Set up GDAL/OGR
                Heron.GdalConfiguration.ConfigureGdal();

                // Make sure global HeronSRS is being used.
                GeoUtils.SetSpatialReferences();

                List<GH_Point> poisGH = new List<GH_Point>(); // Allow for null point values
                List<Point3d> pois = new List<Point3d>();
                PointCloud poiPc = null;
                Curve boundary = null;
                int maxLod = 4;
                int maxSse = 16;
                int k = 600;
                string cacheFolder = null;
                string apiKey = null;
                bool download = false;

                // Order requested: POIs, Boundary, LOD, MaxSSE, K, Folder, API Key, Download
                da.GetDataList(0, poisGH);
                if (!da.GetData(1, ref boundary) || boundary == null)
                {
                    Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary is required.");
                    return;
                }

                // Early bounding box validity check like other components
                var rawBBox = boundary.GetBoundingBox(true);
                if (!rawBBox.IsValid || rawBBox.Diagonal.Length <= modelTol)
                {
                    Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary bounding box invalid or too small.");
                    return;
                }
                da.GetData(2, ref maxLod);
                da.GetData(3, ref maxSse);
                da.GetData(4, ref k);
                da.GetData(5, ref cacheFolder);
                if (!da.GetData(6, ref apiKey) || string.IsNullOrWhiteSpace(apiKey))
                {
                    var envKey = System.Environment.GetEnvironmentVariable("HERONGOOGLEAPIKEY");
                    if (envKey is string && !string.IsNullOrWhiteSpace(envKey))
                    {
                        Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Using HERONGOOGLEAPIKEY from Environmental Variables.");
                        apiKey = envKey;
                    }
                    else
                    {
                        Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "API Key is required.");
                        return;
                    }
                }
                da.GetData(7, ref download);

                for (int i = 0; i < poisGH.Count; i++)
                {
                    if (poisGH[i] != null)
                    {
                        pois.Add(poisGH[i].Value);
                        poiPc.Add(GeoUtils.ModelToEcefPoint(poisGH[i].Value));
                    }
                    else
                    {
                        Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid Point of Interest at index " + i);
                        return;
                    }
                }

                if (!boundary.TryGetPlane(out var boundaryPlane))
                {
                    Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary must be planar.");
                    return;
                }
                if (maxLod < 0)
                {
                    Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Max LOD must be >= 0.");
                    return;
                }
                if (maxSse < 1)
                {
                    Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Max Screen Space Error must be >= 1.");
                    return;
                }
                if (k < 1)
                {
                    Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Constant K must be >= 1.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(cacheFolder))
                {
                    Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Cache Folder is required.");
                    return;
                }

                if (!Directory.Exists(cacheFolder)) Directory.CreateDirectory(cacheFolder);

                // Create AOI polyline *before* computing manifest metrics to ensure consistency
                var aoi = GeoUtils.GetAoiBoundingPolyline(boundary);

                if (aoi == null || !aoi.IsClosed)
                {
                    Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Boundary must be a closed planar curve.");
                    return;
                }

                PoisGH = poisGH;
                Pois = pois;
                PoiPc = poiPc;
                Boundary = boundary;
                Aoi = aoi;
                MaxLod = maxLod;
                MaxSse = maxSse;
                K = k;
                CacheFolder = cacheFolder;
                ApiKey = apiKey;
                Download = download;

            }

            public override void SetData(IGH_DataAccess da)
            {
                if (TileBoundingBoxes != null && TileBoundingBoxes.Count > 0)
                {
                    List<GH_Box> ghBoxes = new List<GH_Box>();
                    foreach (var box in TileBoundingBoxes)
                    {
                        ghBoxes.Add(new GH_Box(box));
                    }
                    da.SetDataList(0, ghBoxes);
                }
            }
        }
    }

    // Minimal Data Models for 2025 OGC glTF standard
    public class Tileset { public Tile root { get; set; } }
    public class Tile
    {
        public double GeometricError { get; set; }
        public BoundingVolume BoundingVolume { get; set; }
        public Content Content { get; set; }
        public List<Tile> Children { get; set; }

        /// <summary>
        /// Convert this tile's bounding volume "box" (12-double array per 3D Tiles spec)
        /// into a Rhino.Geometry.Box (or null if not available/invalid).
        /// Expected format: [center.x, center.y, center.z, ux,uy,uz, vx,vy,vz, wx,wy,wz]
        /// where ux/uy/uz etc. are the oriented half-axes vectors.
        /// </summary>
        public Rhino.Geometry.Box? GetEcefBox()
        {
            var boxArray = BoundingVolume?.Box;
            if (boxArray == null || boxArray.Length < 12)
                return null;

            // center
            var center = new Rhino.Geometry.Point3d(boxArray[0], boxArray[1], boxArray[2]);

            // axis vectors (these represent oriented half-axes; use their lengths as extents)
            var xDir = new Rhino.Geometry.Vector3d(boxArray[3], boxArray[4], boxArray[5]);
            var yDir = new Rhino.Geometry.Vector3d(boxArray[6], boxArray[7], boxArray[8]);
            var zDir = new Rhino.Geometry.Vector3d(boxArray[9], boxArray[10], boxArray[11]);

            // Create intervals based on axis lengths (symmetric around center)
            var xInterval = new Rhino.Geometry.Interval(-xDir.Length, xDir.Length);
            var yInterval = new Rhino.Geometry.Interval(-yDir.Length, yDir.Length);
            var zInterval = new Rhino.Geometry.Interval(-zDir.Length, zDir.Length);

            // Construct a plane from center using x and y axis directions for orientation.
            // If xDir or yDir are degenerate, fallback to world XY.
            Rhino.Geometry.Plane plane;
            if (!new Rhino.Geometry.Plane(center, xDir, yDir).IsValid)
                plane = new Rhino.Geometry.Plane(center, Rhino.Geometry.Vector3d.XAxis, Rhino.Geometry.Vector3d.YAxis);
            else
                plane = new Rhino.Geometry.Plane(center, xDir, yDir);

            return new Rhino.Geometry.Box(plane, xInterval, yInterval, zInterval);
        }
    }
    public class BoundingVolume 
    { 
        public double[] Box { get; set; } 
    }

    public class Content { public string Uri { get; set; } }
}