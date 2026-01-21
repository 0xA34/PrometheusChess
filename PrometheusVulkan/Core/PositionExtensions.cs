using ChessCore.Models;

namespace PrometheusVulkan.Core;

public static class PositionExtensions
{
    public static int CalculateIndex(this Position pos)
    {
        return pos.Row * 8 + pos.Col;
    }

    public static Position FromIndex(int index)
    {
        return new Position(index / 8, index % 8);
    }
}
