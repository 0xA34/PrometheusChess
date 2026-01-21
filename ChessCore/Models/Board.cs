namespace ChessCore.Models;

/// <summary>
/// Represents the chess board state
/// </summary>
public sealed class Board
{
    /// <summary>
    /// 8x8 array representing the board. Index [row, col] where row 0 is rank 1 (white side)
    /// </summary>
    private readonly ChessPiece?[,] _squares = new ChessPiece?[8, 8];

    /// <summary>
    /// List of all pieces currently on the board
    /// </summary>
    private readonly List<ChessPiece> _pieces = new();

    /// <summary>
    /// The position where en passant capture is possible (null if not available)
    /// </summary>
    public Position? EnPassantTarget { get; private set; }

    /// <summary>
    /// Whose turn it is to move
    /// </summary>
    public PieceColor CurrentTurn { get; private set; } = PieceColor.White;

    /// <summary>
    /// Number of half-moves since the last pawn move or capture (for 50-move rule)
    /// </summary>
    public int HalfMoveClock { get; private set; }

    /// <summary>
    /// Full move number (starts at 1, increments after black's move)
    /// </summary>
    public int FullMoveNumber { get; private set; } = 1;

    /// <summary>
    /// White's castling rights
    /// </summary>
    public CastlingRights WhiteCastlingRights { get; private set; } = CastlingRights.Both;

    /// <summary>
    /// Black's castling rights
    /// </summary>
    public CastlingRights BlackCastlingRights { get; private set; } = CastlingRights.Both;

    /// <summary>
    /// Gets all pieces currently on the board (read-only)
    /// </summary>
    public IReadOnlyList<ChessPiece> Pieces => _pieces.AsReadOnly();

    /// <summary>
    /// Gets all white pieces
    /// </summary>
    public IEnumerable<ChessPiece> WhitePieces => _pieces.Where(p => p.Color == PieceColor.White);

    /// <summary>
    /// Gets all black pieces
    /// </summary>
    public IEnumerable<ChessPiece> BlackPieces => _pieces.Where(p => p.Color == PieceColor.Black);

    /// <summary>
    /// Creates an empty board
    /// </summary>
    public Board()
    {
    }

    /// <summary>
    /// Creates a board with the standard starting position
    /// </summary>
    public static Board CreateStartingPosition()
    {
        var board = new Board();
        board.SetupStandardPosition();
        return board;
    }

    /// <summary>
    /// Creates a board from a FEN string
    /// </summary>
    /// <param name="fen">FEN notation string</param>
    /// <returns>A new Board representing the position</returns>
    public static Board FromFen(string fen)
    {
        var board = new Board();
        board.LoadFromFen(fen);
        return board;
    }

    /// <summary>
    /// Gets the piece at the specified position, or null if empty
    /// </summary>
    public ChessPiece? GetPieceAt(Position position)
    {
        if (!position.IsValid)
            return null;

        return _squares[position.Row, position.Col];
    }

    /// <summary>
    /// Gets the piece at the specified row and column, or null if empty
    /// </summary>
    public ChessPiece? GetPieceAt(int row, int col)
    {
        if (row < 0 || row > 7 || col < 0 || col > 7)
            return null;

        return _squares[row, col];
    }

    /// <summary>
    /// Returns true if the specified position is empty
    /// </summary>
    public bool IsEmpty(Position position)
    {
        return GetPieceAt(position) == null;
    }

    /// <summary>
    /// Returns true if the specified position contains a piece of the given color
    /// </summary>
    public bool HasPieceOfColor(Position position, PieceColor color)
    {
        var piece = GetPieceAt(position);
        return piece != null && piece.Color == color;
    }

    /// <summary>
    /// Returns true if the specified position contains an opponent's piece
    /// </summary>
    public bool HasOpponentPiece(Position position, PieceColor myColor)
    {
        var piece = GetPieceAt(position);
        return piece != null && piece.Color != myColor;
    }

    /// <summary>
    /// Finds the king of the specified color
    /// </summary>
    public ChessPiece? FindKing(PieceColor color)
    {
        return _pieces.FirstOrDefault(p => p.Type == PieceType.King && p.Color == color);
    }

    /// <summary>
    /// Gets all pieces of the specified type and color
    /// </summary>
    public IEnumerable<ChessPiece> GetPieces(PieceType type, PieceColor color)
    {
        return _pieces.Where(p => p.Type == type && p.Color == color);
    }

    /// <summary>
    /// Gets all pieces of the specified color
    /// </summary>
    public IEnumerable<ChessPiece> GetPieces(PieceColor color)
    {
        return _pieces.Where(p => p.Color == color);
    }

    /// <summary>
    /// Places a piece on the board
    /// </summary>
    /// <param name="piece">The piece to place</param>
    /// <exception cref="InvalidOperationException">Thrown if the position is already occupied</exception>
    public void PlacePiece(ChessPiece piece)
    {
        if (!piece.Position.IsValid)
            throw new ArgumentException("Invalid piece position", nameof(piece));

        var existing = GetPieceAt(piece.Position);
        if (existing != null)
            throw new InvalidOperationException($"Position {piece.Position} is already occupied by {existing}");

        _squares[piece.Position.Row, piece.Position.Col] = piece;
        _pieces.Add(piece);
    }

    /// <summary>
    /// Removes a piece from the board
    /// </summary>
    /// <param name="position">The position to clear</param>
    /// <returns>The removed piece, or null if position was empty</returns>
    public ChessPiece? RemovePiece(Position position)
    {
        if (!position.IsValid)
            return null;

        var piece = _squares[position.Row, position.Col];
        if (piece != null)
        {
            _squares[position.Row, position.Col] = null;
            _pieces.Remove(piece);
        }

        return piece;
    }

    /// <summary>
    /// Moves a piece from one position to another (internal use - does not validate)
    /// </summary>
    internal void MovePieceInternal(Position from, Position to)
    {
        var piece = RemovePiece(from);
        if (piece == null)
            throw new InvalidOperationException($"No piece at {from}");

        // Remove any piece at the destination (capture)
        RemovePiece(to);

        // Update piece position
        piece.MoveTo(to);

        // Place at new position
        _squares[to.Row, to.Col] = piece;
        _pieces.Add(piece);
    }

    /// <summary>
    /// Sets up the standard starting chess position
    /// </summary>
    public void SetupStandardPosition()
    {
        Clear();

        // White pieces (row 0 and 1)
        PlacePiece(new ChessPiece(PieceType.Rook, PieceColor.White, Position.A1));
        PlacePiece(new ChessPiece(PieceType.Knight, PieceColor.White, Position.B1));
        PlacePiece(new ChessPiece(PieceType.Bishop, PieceColor.White, Position.C1));
        PlacePiece(new ChessPiece(PieceType.Queen, PieceColor.White, new Position(0, 3)));
        PlacePiece(new ChessPiece(PieceType.King, PieceColor.White, Position.E1));
        PlacePiece(new ChessPiece(PieceType.Bishop, PieceColor.White, Position.F1));
        PlacePiece(new ChessPiece(PieceType.Knight, PieceColor.White, Position.G1));
        PlacePiece(new ChessPiece(PieceType.Rook, PieceColor.White, Position.H1));

        for (int col = 0; col < 8; col++)
        {
            PlacePiece(new ChessPiece(PieceType.Pawn, PieceColor.White, new Position(1, col)));
        }

        // Black pieces (row 7 and 6)
        PlacePiece(new ChessPiece(PieceType.Rook, PieceColor.Black, Position.A8));
        PlacePiece(new ChessPiece(PieceType.Knight, PieceColor.Black, Position.B8));
        PlacePiece(new ChessPiece(PieceType.Bishop, PieceColor.Black, Position.C8));
        PlacePiece(new ChessPiece(PieceType.Queen, PieceColor.Black, new Position(7, 3)));
        PlacePiece(new ChessPiece(PieceType.King, PieceColor.Black, Position.E8));
        PlacePiece(new ChessPiece(PieceType.Bishop, PieceColor.Black, Position.F8));
        PlacePiece(new ChessPiece(PieceType.Knight, PieceColor.Black, Position.G8));
        PlacePiece(new ChessPiece(PieceType.Rook, PieceColor.Black, Position.H8));

        for (int col = 0; col < 8; col++)
        {
            PlacePiece(new ChessPiece(PieceType.Pawn, PieceColor.Black, new Position(6, col)));
        }

        CurrentTurn = PieceColor.White;
        WhiteCastlingRights = CastlingRights.Both;
        BlackCastlingRights = CastlingRights.Both;
        EnPassantTarget = null;
        HalfMoveClock = 0;
        FullMoveNumber = 1;
    }

    /// <summary>
    /// Clears all pieces from the board
    /// </summary>
    public void Clear()
    {
        Array.Clear(_squares);
        _pieces.Clear();
        EnPassantTarget = null;
        CurrentTurn = PieceColor.White;
        HalfMoveClock = 0;
        FullMoveNumber = 1;
        WhiteCastlingRights = CastlingRights.None;
        BlackCastlingRights = CastlingRights.None;
    }

    /// <summary>
    /// Loads a position from FEN notation
    /// </summary>
    /// <param name="fen">FEN notation string</param>
    public void LoadFromFen(string fen)
    {
        Clear();

        var parts = fen.Split(' ');
        if (parts.Length < 1)
            throw new ArgumentException("Invalid FEN string", nameof(fen));

        // Parse piece placement
        var ranks = parts[0].Split('/');
        if (ranks.Length != 8)
            throw new ArgumentException("FEN must have 8 ranks", nameof(fen));

        for (int rank = 7; rank >= 0; rank--)
        {
            int col = 0;
            foreach (char c in ranks[7 - rank])
            {
                if (char.IsDigit(c))
                {
                    col += c - '0';
                }
                else
                {
                    var piece = ChessPiece.FromFenChar(c, new Position(rank, col));
                    if (piece != null)
                    {
                        PlacePiece(piece);
                    }
                    col++;
                }
            }
        }

        // Parse active color
        if (parts.Length > 1)
        {
            CurrentTurn = parts[1].ToLower() == "b" ? PieceColor.Black : PieceColor.White;
        }

        // Parse castling rights
        if (parts.Length > 2)
        {
            WhiteCastlingRights = CastlingRights.None;
            BlackCastlingRights = CastlingRights.None;

            if (parts[2].Contains('K'))
                WhiteCastlingRights |= CastlingRights.Kingside;
            if (parts[2].Contains('Q'))
                WhiteCastlingRights |= CastlingRights.Queenside;
            if (parts[2].Contains('k'))
                BlackCastlingRights |= CastlingRights.Kingside;
            if (parts[2].Contains('q'))
                BlackCastlingRights |= CastlingRights.Queenside;
        }

        // Parse en passant target
        if (parts.Length > 3 && parts[3] != "-")
        {
            if (Position.TryFromAlgebraic(parts[3], out var epTarget))
            {
                EnPassantTarget = epTarget;
            }
        }

        // Parse half-move clock
        if (parts.Length > 4 && int.TryParse(parts[4], out int halfMove))
        {
            HalfMoveClock = halfMove;
        }

        // Parse full move number
        if (parts.Length > 5 && int.TryParse(parts[5], out int fullMove))
        {
            FullMoveNumber = fullMove;
        }
    }

    /// <summary>
    /// Exports the current position to FEN notation
    /// </summary>
    /// <returns>FEN string representing the current position</returns>
    public string ToFen()
    {
        var sb = new System.Text.StringBuilder();

        // Piece placement
        for (int rank = 7; rank >= 0; rank--)
        {
            int emptyCount = 0;

            for (int col = 0; col < 8; col++)
            {
                var piece = GetPieceAt(rank, col);
                if (piece == null)
                {
                    emptyCount++;
                }
                else
                {
                    if (emptyCount > 0)
                    {
                        sb.Append(emptyCount);
                        emptyCount = 0;
                    }
                    sb.Append(piece.ToFenChar());
                }
            }

            if (emptyCount > 0)
            {
                sb.Append(emptyCount);
            }

            if (rank > 0)
            {
                sb.Append('/');
            }
        }

        // Active color
        sb.Append(' ');
        sb.Append(CurrentTurn == PieceColor.White ? 'w' : 'b');

        // Castling rights
        sb.Append(' ');
        var castling = "";
        if (WhiteCastlingRights.HasFlag(CastlingRights.Kingside)) castling += "K";
        if (WhiteCastlingRights.HasFlag(CastlingRights.Queenside)) castling += "Q";
        if (BlackCastlingRights.HasFlag(CastlingRights.Kingside)) castling += "k";
        if (BlackCastlingRights.HasFlag(CastlingRights.Queenside)) castling += "q";
        sb.Append(string.IsNullOrEmpty(castling) ? "-" : castling);

        // En passant target
        sb.Append(' ');
        sb.Append(EnPassantTarget?.ToAlgebraic() ?? "-");

        // Half-move clock
        sb.Append(' ');
        sb.Append(HalfMoveClock);

        // Full move number
        sb.Append(' ');
        sb.Append(FullMoveNumber);

        return sb.ToString();
    }

    /// <summary>
    /// Creates a deep copy of this board
    /// </summary>
    public Board Clone()
    {
        var clone = new Board
        {
            CurrentTurn = CurrentTurn,
            EnPassantTarget = EnPassantTarget,
            HalfMoveClock = HalfMoveClock,
            FullMoveNumber = FullMoveNumber,
            WhiteCastlingRights = WhiteCastlingRights,
            BlackCastlingRights = BlackCastlingRights
        };

        foreach (var piece in _pieces)
        {
            clone.PlacePiece(piece.Clone());
        }

        return clone;
    }

    /// <summary>
    /// Sets the current turn
    /// </summary>
    public void SetCurrentTurn(PieceColor color)
    {
        CurrentTurn = color;
    }

    /// <summary>
    /// Switches the current turn to the other player
    /// </summary>
    public void SwitchTurn()
    {
        CurrentTurn = CurrentTurn == PieceColor.White ? PieceColor.Black : PieceColor.White;
        if (CurrentTurn == PieceColor.White)
        {
            FullMoveNumber++;
        }
    }

    /// <summary>
    /// Sets the en passant target square
    /// </summary>
    public void SetEnPassantTarget(Position? position)
    {
        EnPassantTarget = position;
    }

    /// <summary>
    /// Increments the half-move clock
    /// </summary>
    public void IncrementHalfMoveClock()
    {
        HalfMoveClock++;
    }

    /// <summary>
    /// Resets the half-move clock (after pawn move or capture)
    /// </summary>
    public void ResetHalfMoveClock()
    {
        HalfMoveClock = 0;
    }

    /// <summary>
    /// Updates castling rights based on a piece moving
    /// </summary>
    public void UpdateCastlingRights(ChessPiece piece, Position from)
    {
        // King moved - lose all castling rights
        if (piece.Type == PieceType.King)
        {
            if (piece.Color == PieceColor.White)
                WhiteCastlingRights = CastlingRights.None;
            else
                BlackCastlingRights = CastlingRights.None;
        }

        // Rook moved or captured - lose that side's castling rights
        if (piece.Type == PieceType.Rook || from.Row == 0 || from.Row == 7)
        {
            // Check if a rook moved from its starting position
            if (from == Position.A1)
                WhiteCastlingRights &= ~CastlingRights.Queenside;
            else if (from == Position.H1)
                WhiteCastlingRights &= ~CastlingRights.Kingside;
            else if (from == Position.A8)
                BlackCastlingRights &= ~CastlingRights.Queenside;
            else if (from == Position.H8)
                BlackCastlingRights &= ~CastlingRights.Kingside;
        }
    }

    /// <summary>
    /// Gets castling rights for the specified color
    /// </summary>
    public CastlingRights GetCastlingRights(PieceColor color)
    {
        return color == PieceColor.White ? WhiteCastlingRights : BlackCastlingRights;
    }

    /// <summary>
    /// Returns a simple ASCII representation of the board
    /// </summary>
    public string ToAscii()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("  +---+---+---+---+---+---+---+---+");

        for (int rank = 7; rank >= 0; rank--)
        {
            sb.Append($"{rank + 1} |");
            for (int col = 0; col < 8; col++)
            {
                var piece = GetPieceAt(rank, col);
                char c = piece?.ToFenChar() ?? '.';
                sb.Append($" {c} |");
            }
            sb.AppendLine();
            sb.AppendLine("  +---+---+---+---+---+---+---+---+");
        }

        sb.AppendLine("    a   b   c   d   e   f   g   h");
        return sb.ToString();
    }

    /// <summary>
    /// Calculates the total material value for the specified color
    /// </summary>
    public int GetMaterialValue(PieceColor color)
    {
        return GetPieces(color).Sum(p => p.MaterialValue);
    }

    /// <summary>
    /// Returns the material difference (positive = white advantage)
    /// </summary>
    public int GetMaterialBalance()
    {
        return GetMaterialValue(PieceColor.White) - GetMaterialValue(PieceColor.Black);
    }

    public override string ToString()
    {
        return ToFen();
    }
}

/// <summary>
/// Castling availability flags
/// </summary>
[Flags]
public enum CastlingRights
{
    None = 0,
    Kingside = 1,
    Queenside = 2,
    Both = Kingside | Queenside
}
