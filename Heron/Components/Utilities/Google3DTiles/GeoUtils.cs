using OSGeo.OGR;
using OSGeo.OSR;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Render.ChangeQueue;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using og = OSGeo.OSR;

namespace Heron.Utilities.Google3DTiles
{
    public static partial class GeoUtils
    {
        public static OSGeo.OSR.CoordinateTransformation _ecefToLatLonHeightTransform = null;
        public static OSGeo.OSR.CoordinateTransformation _latLonHeightToEcefTransform = null;
        public static OSGeo.OSR.CoordinateTransformation _ecefToGeoidTransform = null;
        public static OSGeo.OSR.SpatialReference _wgsEcefSrs = null;
        public static OSGeo.OSR.SpatialReference _wgsLatLonHeightSrs = null;
        public static OSGeo.OSR.SpatialReference _wgsGeoidSrs = null;

        public static bool SetSpatialReferences()
        {
            // Ensure GDAL is configured
            Heron.GdalConfiguration.ConfigureOgr();

            if (_wgsEcefSrs != null && _wgsLatLonHeightSrs != null) { return true; }
            else
            {
                _wgsEcefSrs = new SpatialReference("");
                _wgsEcefSrs.ImportFromEPSG(4978); // WGS 84 ECEF
                _wgsLatLonHeightSrs = new SpatialReference("");
                _wgsLatLonHeightSrs.ImportFromEPSG(4979); // WGS 84 Geodetic (Lat/Lon/Height)

                /// Google 3D Tiles seem to use heights relative to mean sea level (geoid) rather than an ellipsoid
                /// Employ a custom PROJ string to use in the worldwide EGM2008 geoid height model from OSGeo as the vertical datum.
                /// The geoid gtx file is downloaded if not found in the target directory per the DownloadGeoidFile Target in Heron.csproj.
                /// This avoids the large gtx file from being checked into source control.
                /// Geoid source: https://github.com/OSGeo/proj-datumgrid/blob/master/world/egm08_25.gtx (142mb file)
                /// See also: https://www.unavco.org/software/geodetic-utilities/geoid-height-calculator/geoid-height-calculator.html for geoid vs ellipsoid

                var geoidgridsLocation = Path.Combine(HeronLocation.GetHeronFolder(), "gdal", "share", "egm08_25.gtx");
                _wgsGeoidSrs = new SpatialReference("");
                _wgsGeoidSrs.SetFromUserInput("+proj=longlat +datum=WGS84 +no_defs +geoidgrids=" + geoidgridsLocation);

                /// TODO: Set up HeronSRS to work with these transfomations

                return true;
            }
        }
        public static OSGeo.OSR.CoordinateTransformation SetTransformEcefToLatLonHeight()
        {
            if (_ecefToLatLonHeightTransform != null) return _ecefToLatLonHeightTransform;
            else
            {
                if (_wgsEcefSrs == null || _wgsLatLonHeightSrs == null) SetSpatialReferences();
                _ecefToLatLonHeightTransform = new OSGeo.OSR.CoordinateTransformation(_wgsEcefSrs, _wgsLatLonHeightSrs);
                return _ecefToLatLonHeightTransform;
            }
        }

        public static OSGeo.OSR.CoordinateTransformation SetTransformLatLonHeightToEcef()
        {
            if (_latLonHeightToEcefTransform != null) return _latLonHeightToEcefTransform;
            else
            {
                if (_wgsEcefSrs == null || _wgsLatLonHeightSrs == null) SetSpatialReferences();
                _latLonHeightToEcefTransform = new OSGeo.OSR.CoordinateTransformation(_wgsLatLonHeightSrs, _wgsEcefSrs);
                return _latLonHeightToEcefTransform;
            }
        }

        public static OSGeo.OSR.CoordinateTransformation SetTransformEcefToGeoid()
        {
            if (_ecefToGeoidTransform != null) return _ecefToGeoidTransform;
            else
            {
                if (_wgsEcefSrs == null || _wgsGeoidSrs == null) SetSpatialReferences();
                _ecefToGeoidTransform = new OSGeo.OSR.CoordinateTransformation(_wgsEcefSrs, _wgsGeoidSrs);
                return _ecefToGeoidTransform;
            }
        }


        // Build min/max lon/lat via Heron for Info
        public static (double minLon, double minLat, double maxLon, double maxLat) AoiToWgs(Rhino.Geometry.Polyline pl)
        {
            double minLon = double.PositiveInfinity, minLat = double.PositiveInfinity;
            double maxLon = double.NegativeInfinity, maxLat = double.NegativeInfinity;

            foreach (var p in pl)
            {
                Point3d w;
                try
                {
                    w = Heron.Convert.XYZToWGS(p); // Heron EAP → WGS84
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Failed to convert model coordinates to WGS84. This typically indicates that RhinoDoc.ActiveDoc or EarthAnchorPoint is null, " +
                        "disposed, or improperly initialized. Ensure Rhino document is open and EarthAnchorPoint is set using Heron's SetEAP component.", ex);
                }
                
                minLon = Math.Min(minLon, w.X);
                maxLon = Math.Max(maxLon, w.X);
                minLat = Math.Min(minLat, w.Y);
                maxLat = Math.Max(maxLat, w.Y);
            }

            return (minLon, minLat, maxLon, maxLat);
        }

        public static List<Point3d> AoiToWgsGdal(Polyline aoiModel)
        {
            var wgsList = new List<Point3d>();
            foreach (var p in aoiModel)
            {
                Point3d w;
                try
                {
                    w = Heron.Convert.XYZToWGS(p); // Heron EAP → WGS84
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Failed to convert model coordinates to WGS84. This typically indicates that RhinoDoc.ActiveDoc or EarthAnchorPoint is null, " +
                        "disposed, or improperly initialized. Ensure Rhino document is open and EarthAnchorPoint is set using Heron's SetEAP component.", ex);
                }
                wgsList.Add(w);
            }

            // Convert to ECEF once
            var ecefPoints = new List<Point3d>();
            foreach (var wgs in wgsList)
            {
                try
                {
                    // FIX: Heron convention is w.X=lon, w.Y=lat, so pass correctly to Wgs84ToEcef(lon, lat, h)
                    var ecef = Wgs84ToEcefGdal(wgs.X, wgs.Y, wgs.Z); // lon, lat, h
                    ecefPoints.Add(new Point3d(ecef.X, ecef.Y, ecef.Z));
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to convert WGS84 coordinates to ECEF at point lon={wgs.X}, lat={wgs.Y}, h={wgs.Z}.", ex);
                }
            }

            return ecefPoints;
        }

        public static Polyline GetAoiBoundingPolyline(Curve boundary)
        {
            BoundingBox bbox = boundary.GetBoundingBox(false);

            if (!bbox.IsValid)
                return null;

            Point3d[] corners = bbox.GetCorners();

            Point3d[] rectanglePoints = new Point3d[]
            {
                corners[0], // MinX, MinY, MinZ
                corners[1], // MaxX, MinY, MinZ
                corners[2], // MaxX, MaxY, MinZ
                corners[3], // MinX, MaxY, MinZ
                corners[0]  // Close the loop back to the start
            };

            Polyline boundingPolyline = new Polyline(rectanglePoints);
            return boundingPolyline;
        }

        /// <summary>
        /// Converts Earth-Centered, Earth-Fixed (ECEF) Cartesian coordinates (meters) to
        /// geodetic WGS84 longitude (deg), latitude (deg) and ellipsoidal height (meters).
        /// </summary>
        /// <param name="ecef">
        /// Input point expressed in the global ECEF frame:
        /// X axis → intersection of equator and prime meridian,
        /// Y axis → 90° East along equator,
        /// Z axis → North pole.
        /// Units: meters.
        /// </param>
        /// <returns>
        /// A tuple (lonDeg, latDeg, h):
        ///   lonDeg: longitude in degrees in range [-180, 180)
        ///   latDeg: geodetic latitude in degrees (−90 to +90)
        ///   h: ellipsoidal height above the WGS84 reference ellipsoid in meters.
        /// </returns>
        public static (double lonDeg, double latDeg, double h) EcefToWgs84Gdal(Point3d ecef)
        {
            if (_ecefToLatLonHeightTransform == null) SetTransformEcefToLatLonHeight();

            double[] point = new double[] { ecef.X, ecef.Y, ecef.Z };
            _ecefToLatLonHeightTransform.TransformPoint(point);

            // Note: GDAL typically returns coordinates in the order defined by the SRS authority
            // for EPSG:4979 this is Latitude, then Longitude, then Height.
            double lat = point[1]; // Latitude in degrees
            double lon = point[0]; // Longitude in degrees
            double height = point[2]; // Height in meters

            return (lon,lat,height);
        }

        public static (double latDeg, double lonDeg, double h) EcefToGeoidGdal(Point3d ecef)
        {
            if (_ecefToGeoidTransform == null) SetTransformEcefToGeoid();

            double[] point = new double[] { ecef.X, ecef.Y, ecef.Z };
            _ecefToGeoidTransform.TransformPoint(point);

            // Note: GDAL typically returns coordinates in the order defined by the SRS authority
            // for EPSG:4979 this is Latitude, then Longitude, then Height.
            double lat = point[0]; // Latitude in degrees
            double lon = point[1]; // Longitude in degrees
            double height = point[2]; // Height in meters

            return (lon, lat, height);
        }      

        /// <summary>
        /// Helper: convert WGS84 (lon°, lat°, h_m) to ECEF (X,Y,Z meters).
        /// </summary>
        public static Point3d Wgs84ToEcefGdal(double lonDeg, double latDeg, double hMeters)
        {
            if (_latLonHeightToEcefTransform == null) SetTransformLatLonHeightToEcef();
            
            double[] point = new double[] { lonDeg, latDeg, hMeters };
            _latLonHeightToEcefTransform.TransformPoint(point);
            // Note: GDAL typically expects coordinates in the order defined by the SRS authority
            // for EPSG:4979 this is Latitude, then Longitude, then Height.
            double x = point[0]; // X in meters
            double y = point[1]; // Y in meters
            double z = point[2]; // Z in meters
            
            return new Point3d(x, y, z);
        }
    }
}
