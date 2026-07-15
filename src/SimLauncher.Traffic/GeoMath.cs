namespace SimLauncher.Traffic;

public static class GeoMath
{
    public const double EarthRadiusNm = 3440.065;

    /// <summary>Great-circle distance between two lat/lon points, in nautical miles.</summary>
    public static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = Deg2Rad(lat1);
        var phi2 = Deg2Rad(lat2);
        var dPhi = Deg2Rad(lat2 - lat1);
        var dLambda = Deg2Rad(lon2 - lon1);

        var a = Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2)
                + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);
        return EarthRadiusNm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Initial great-circle bearing from point 1 to point 2, degrees true [0, 360).</summary>
    public static double InitialBearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = Deg2Rad(lat1);
        var phi2 = Deg2Rad(lat2);
        var dLambda = Deg2Rad(lon2 - lon1);

        var y = Math.Sin(dLambda) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);
        var deg = Rad2Deg(Math.Atan2(y, x));
        return (deg + 360) % 360;
    }

    /// <summary>Smallest absolute difference between two headings, degrees [0, 180].</summary>
    public static double HeadingDifferenceDeg(double a, double b)
    {
        var diff = Math.Abs(a - b) % 360;
        return diff > 180 ? 360 - diff : diff;
    }

    private static double Deg2Rad(double deg) => deg * Math.PI / 180.0;
    private static double Rad2Deg(double rad) => rad * 180.0 / Math.PI;
}
