namespace ChessCore.Models;

/// <summary>
/// Represents a chess piece on the board
/// </summary>
public sealed class ChessPiece : IEquatable<ChessPiece>
{
    /// <summary>
    /// The type of this piece (Pawn, Knight, Bishop, etc.)
    /// </summary>
    public PieceType Type { get; }

    /// <summary>
    /// The color of this piece (White or Black)
    /// </summary>
    public PieceColor Color { get; }

    /// <summary>
    /// Current position on the board
    /// </summary>
    public Position Position { get; private set; }

    /// <summary>
    /// Whether this piece has moved during the game (important for castling and pawn double-move)
    /// </summary>
    public bool HasMoved { get; private set; }

    /// <summary>
    /// Unique identifier for this piece instance (useful for tracking across network)
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Creates a new chess piece
    /// </summary>
    /// <param name="type">The type of piece</param>
    /// <param name="color">The color of the piece</param>
    /// <param name="position">Starting position on the board</param>
    /// <param name="id">Optional unique identifier (generated if not provided)</param>
    public ChessPiece(PieceType type, PieceColor color, Position position, Guid? id = null)
    {
        Type = type;
        Color = color;
        Position = position;
        HasMoved = false;
        Id = id ?? Guid.NewGuid();
    }

    /// <summary>
    /// Creates a new chess piece that has already moved
    /// </summary>
    private ChessPiece(PieceType type, PieceColor color, Position position, bool hasMoved, Guid id)
    {
        Type = type;
        Color = color;
        Position = position;
        HasMoved = hasMoved;
        Id = id;
    }

    /// <summary>
    /// Moves the piece to a new position and marks it as having moved
    /// </summary>
    /// <param name="newPosition">The destination position</param>
    public void MoveTo(Position newPosition)
    {
        Position = newPosition;
        HasMoved = true;
    }

    /// <summary>
    /// Creates a deep copy of this piece
    /// </summary>
    public ChessPiece Clone()
    {
        return new ChessPiece(Type, Color, Position, HasMoved, Id);
    }

    /// <summary>
    /// Creates a copy of this piece with a new position (for move simulation)
    /// </summary>
    public ChessPiece CloneWithPosition(Position newPosition)
    {
        return new ChessPiece(Type, Color, newPosition, true, Id);
    }

    /// <summary>
    /// Creates a promoted piece (for pawn promotion)
    /// </summary>
    /// <param name="newType">The type to promote to (must be Queen, Rook, Bishop, or Knight)</param>
    /// <returns>A new piece of the specified type at the same position</returns>
    /// <exception cref="ArgumentException">Thrown if promotion type is invalid</exception>
    public ChessPiece Promote(PieceType newType)
    {
        if (Type != PieceType.Pawn)
            throw new InvalidOperationException("Only pawns can be promoted");

        if (newType == PieceType.Pawn || newType == PieceType.King)
            throw new ArgumentException("Cannot promote to Pawn or King", nameof(newType));

        return new ChessPiece(newType, Color, Position, true, Guid.NewGuid());
    }

    /// <summary>
    /// Gets the material value of this piece (for evaluation)
    /// Standard values: Pawn=1, Knight=3, Bishop=3, Rook=5, Queen=9, King=0 (infinite)
    /// </summary>
    public int MaterialValue => Type switch
    {
        PieceType.Pawn => 1,
        PieceType.Knight => 3,
        PieceType.Bishop => 3,
        PieceType.Rook => 5,
        PieceType.Queen => 9,
        PieceType.King => 0, // King has infinite value (game over if lost)
        _ => 0
    };

    /// <summary>
    /// Returns the FEN character for this piece
    /// </summary>
    public char ToFenChar()
    {
        char c = Type switch
        {
            PieceType.Pawn => 'p',
            PieceType.Knight => 'n',
            PieceType.Bishop => 'b',
            PieceType.Rook => 'r',
            PieceType.Queen => 'q',
            PieceType.King => 'k',
            _ => '?'
        };

        return Color == PieceColor.White ? char.ToUpper(c) : c;
    }

    /// <summary>
    /// Creates a piece from a FEN character
    /// </summary>
    /// <param name="fenChar">The FEN character (uppercase=white, lowercase=black)</param>
    /// <param name="position">The position of the piece</param>
    /// <returns>The created piece, or null if invalid character</returns>
    public static ChessPiece? FromFenChar(char fenChar, Position position)
    {
        PieceColor color = char.IsUpper(fenChar) ? PieceColor.White : PieceColor.Black;
        char lower = char.ToLower(fenChar);

        PieceType? type = lower switch
        {
            'p' => PieceType.Pawn,
            'n' => PieceType.Knight,
            'b' => PieceType.Bishop,
            'r' => PieceType.Rook,
            'q' => PieceType.Queen,
            'k' => PieceType.King,
            _ => null
        };

        return type.HasValue ? new ChessPiece(type.Value, color, position) : null;
    }

    /// <summary>
    /// Gets the Unicode symbol for this piece
    /// </summary>
    public string UnicodeSymbol => (Type, Color) switch
    {
        (PieceType.King, PieceColor.White) => "♔",
        (PieceType.Queen, PieceColor.White) => "♕",
        (PieceType.Rook, PieceColor.White) => "♖",
        (PieceType.Bishop, PieceColor.White) => "♗",
        (PieceType.Knight, PieceColor.White) => "♘",
        (PieceType.Pawn, PieceColor.White) => "♙",
        (PieceType.King, PieceColor.Black) => "♚",
        (PieceType.Queen, PieceColor.Black) => "♛",
        (PieceType.Rook, PieceColor.Black) => "♜",
        (PieceType.Bishop, PieceColor.Black) => "♝",
        (PieceType.Knight, PieceColor.Black) => "♞",
        (PieceType.Pawn, PieceColor.Black) => "♟",
        _ => "?"
    };

    /// <summary>
    /// Returns true if this piece belongs to the opponent of the specified color
    /// </summary>
    public bool IsOpponentOf(PieceColor color) => Color != color;

    /// <summary>
    /// Returns true if this piece is a sliding piece (Bishop, Rook, or Queen)
    /// </summary>
    public bool IsSlidingPiece => Type is PieceType.Bishop or PieceType.Rook or PieceType.Queen;

    #region Equality

    public bool Equals(ChessPiece? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id;
    }

    public override bool Equals(object? obj)
    {
        return obj is ChessPiece other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public static bool operator ==(ChessPiece? left, ChessPiece? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    public static bool operator !=(ChessPiece? left, ChessPiece? right)
    {
        return !(left == right);
    }

    #endregion

    public override string ToString()
    {
        return $"{Color} {Type} at {Position}";
    }

    /// <summary>
    /// Returns a short string representation (e.g., "Ke4" for King on e4)
    /// </summary>
    public string ToShortString()
    {
        string pieceChar = Type switch
        {
            PieceType.King => "K",
            PieceType.Queen => "Q",
            PieceType.Rook => "R",
            PieceType.Bishop => "B",
            PieceType.Knight => "N",
            PieceType.Pawn => "",
            _ => "?"
        };

        return $"{pieceChar}{Position.ToAlgebraic()}";
    }
}
