namespace ChessCore.Models;

/// <summary>
/// Represents a position on the chess board using row and column coordinates.
/// Row 0 is the white side (rank 1), Row 7 is the black side (rank 8).
/// Column 0 is the 'a' file, Column 7 is the 'h' file.
/// </summary>
public readonly struct Position : IEquatable<Position>
{
    /// <summary>
    /// Row index (0-7), where 0 is rank 1 (white's back rank) and 7 is rank 8 (black's back rank)
    /// </summary>
    public int Row { get; }

    /// <summary>
    /// Column index (0-7), where 0 is 'a' file and 7 is 'h' file
    /// </summary>
    public int Col { get; }

    /// <summary>
    /// Creates a new position with the specified row and column
    /// </summary>
    public Position(int row, int col)
    {
        Row = row;
        Col = col;
    }

    /// <summary>
    /// Creates a position from algebraic notation (e.g., "e4", "a1")
    /// </summary>
    /// <param name="algebraic">Standard algebraic notation for a square</param>
    /// <returns>The corresponding Position</returns>
    /// <exception cref="ArgumentException">Thrown when the notation is invalid</exception>
    public static Position FromAlgebraic(string algebraic)
    {
        if (string.IsNullOrEmpty(algebraic) || algebraic.Length != 2)
            throw new ArgumentException("Invalid algebraic notation", nameof(algebraic));

        char file = char.ToLower(algebraic[0]);
        char rank = algebraic[1];

        if (file < 'a' || file > 'h')
            throw new ArgumentException($"Invalid file: {file}", nameof(algebraic));

        if (rank < '1' || rank > '8')
            throw new ArgumentException($"Invalid rank: {rank}", nameof(algebraic));

        int col = file - 'a';
        int row = rank - '1';

        return new Position(row, col);
    }

    /// <summary>
    /// Attempts to parse algebraic notation into a Position
    /// </summary>
    public static bool TryFromAlgebraic(string algebraic, out Position position)
    {
        position = default;

        if (string.IsNullOrEmpty(algebraic) || algebraic.Length != 2)
            return false;

        char file = char.ToLower(algebraic[0]);
        char rank = algebraic[1];

        if (file < 'a' || file > 'h' || rank < '1' || rank > '8')
            return false;

        position = new Position(rank - '1', file - 'a');
        return true;
    }

    /// <summary>
    /// Converts to algebraic notation (e.g., "e4")
    /// </summary>
    public string ToAlgebraic()
    {
        if (!IsValid)
            return "??";

        char file = (char)('a' + Col);
        char rank = (char)('1' + Row);
        return $"{file}{rank}";
    }

    /// <summary>
    /// Returns true if this position is within the valid board bounds (0-7 for both row and col)
    /// </summary>
    public bool IsValid => Row >= 0 && Row <= 7 && Col >= 0 && Col <= 7;

    /// <summary>
    /// Returns the rank number (1-8) in standard chess notation
    /// </summary>
    public int Rank => Row + 1;

    /// <summary>
    /// Returns the file letter ('a'-'h') in standard chess notation
    /// </summary>
    public char File => (char)('a' + Col);

    /// <summary>
    /// Returns true if this is a light-colored square
    /// </summary>
    public bool IsLightSquare => (Row + Col) % 2 == 1;

    /// <summary>
    /// Returns true if this is a dark-colored square
    /// </summary>
    public bool IsDarkSquare => (Row + Col) % 2 == 0;

    /// <summary>
    /// Returns a new position offset by the specified delta
    /// </summary>
    public Position Offset(int rowDelta, int colDelta)
    {
        return new Position(Row + rowDelta, Col + colDelta);
    }

    /// <summary>
    /// Calculates the Manhattan distance to another position
    /// </summary>
    public int ManhattanDistanceTo(Position other)
    {
        return Math.Abs(Row - other.Row) + Math.Abs(Col - other.Col);
    }

    /// <summary>
    /// Calculates the Chebyshev distance (king's move distance) to another position
    /// </summary>
    public int ChebyshevDistanceTo(Position other)
    {
        return Math.Max(Math.Abs(Row - other.Row), Math.Abs(Col - other.Col));
    }

    /// <summary>
    /// Returns true if this position is on the same row as another
    /// </summary>
    public bool IsSameRow(Position other) => Row == other.Row;

    /// <summary>
    /// Returns true if this position is on the same column as another
    /// </summary>
    public bool IsSameCol(Position other) => Col == other.Col;

    /// <summary>
    /// Returns true if this position is on the same diagonal as another
    /// </summary>
    public bool IsSameDiagonal(Position other)
    {
        return Math.Abs(Row - other.Row) == Math.Abs(Col - other.Col);
    }

    #region Equality

    public bool Equals(Position other)
    {
        return Row == other.Row && Col == other.Col;
    }

    public override bool Equals(object? obj)
    {
        return obj is Position other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Row, Col);
    }

    public static bool operator ==(Position left, Position right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Position left, Position right)
    {
        return !left.Equals(right);
    }

    #endregion

    public override string ToString()
    {
        return ToAlgebraic();
    }

    #region Common Positions

    public static Position A1 => new(0, 0);
    public static Position B1 => new(0, 1);
    public static Position C1 => new(0, 2);
    public static Position D1 => new(0, 3);
    public static Position E1 => new(0, 4);
    public static Position F1 => new(0, 5);
    public static Position G1 => new(0, 6);
    public static Position H1 => new(0, 7);

    public static Position A8 => new(7, 0);
    public static Position B8 => new(7, 1);
    public static Position C8 => new(7, 2);
    public static Position D8 => new(7, 3);
    public static Position E8 => new(7, 4);
    public static Position F8 => new(7, 5);
    public static Position G8 => new(7, 6);
    public static Position H8 => new(7, 7);

    #endregion
}
