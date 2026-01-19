using System.Collections.Generic;
using UnityEngine;

public static class FormationPatternsGrid
{
    public static List<Vector2Int> Line(int count, int rows)
{
    var result = new List<Vector2Int>(count);

    rows = Mathf.Max(1, rows);

    int unitsPerRow = Mathf.CeilToInt(count / (float)rows);
    int index = 0;

    for (int r = 0; r < rows && index < count; r++)
    {
        int unitsInThisRow = Mathf.Min(unitsPerRow, count - index);
        int half = unitsInThisRow / 2;

        for (int i = 0; i < unitsInThisRow; i++)
        {
            int x = i - half;   // plotis
            int y = r;         // gylis (eilės)

            result.Add(new Vector2Int(x, y));
            index++;
        }
    }

    return result;
}



    public static List<Vector2Int> Box(int count)
    {
        var result = new List<Vector2Int>(count);

        int side = Mathf.CeilToInt(Mathf.Sqrt(count));
        int half = side / 2;

        int added = 0;
        for (int y = 0; y < side && added < count; y++)
        {
            for (int x = 0; x < side && added < count; x++)
            {
                int gx = x - half;
                int gy = y - half;
                result.Add(new Vector2Int(gx, gy));
                added++;
            }
        }

        return result;
    }

    public static List<Vector2Int> Wedge(int count)
    {
        var result = new List<Vector2Int>(count);

        int used = 0;
        int row = 0;

        while (used < count)
        {
            int width = row * 2 + 1;
            int half = width / 2;

            for (int x = -half; x <= half && used < count; x++)
            {
                result.Add(new Vector2Int(x, -row));
                used++;
            }

            row++;
        }

        return result;
    }
}