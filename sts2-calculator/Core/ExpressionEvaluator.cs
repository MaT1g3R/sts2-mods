using System;
using System.Collections.Generic;
using System.Globalization;

namespace Sts2Calculator.Core;

/// <summary>
/// Result of evaluating a mathematical expression.
/// </summary>
public readonly record struct EvaluationResult(
	bool IsSuccess,
	double Value,
	string FormattedValue,
	string? ErrorMessage
)
{
	public static EvaluationResult Success(double value) =>
		new(true, value, ExpressionEvaluator.FormatNumber(value), null);

	public static EvaluationResult Failure(string error) =>
		new(false, double.NaN, string.Empty, error);
}

/// <summary>
/// Parses and evaluates mathematical expressions following standard order of operations (PEMDAS/BODMAS).
/// Supports:
/// - Addition (+)
/// - Subtraction (- and unary -)
/// - Multiplication (* or x / X / ×)
/// - Division (/ or ÷)
/// - Grouping brackets ()
/// - Floor functions: floor(...), fl(...), int(...)
/// </summary>
public static class ExpressionEvaluator
{
	private enum TokenType
	{
		Number,
		Plus,
		Minus,
		Multiply,
		Divide,
		LeftParen,
		RightParen,
		FloorFunc,
		End
	}

	private readonly record struct Token(TokenType Type, double NumberValue, string Text, int Position);

	/// <summary>
	/// Evaluates the given mathematical expression string.
	/// </summary>
	public static EvaluationResult Evaluate(string? expression)
	{
		if (string.IsNullOrWhiteSpace(expression))
		{
			return EvaluationResult.Failure("Empty expression");
		}

		if (!TryTokenize(expression, out List<Token> tokens, out string? lexError))
		{
			return EvaluationResult.Failure(lexError ?? "Invalid expression");
		}

		try
		{
			var parser = new Parser(tokens);
			double result = parser.ParseExpression();
			parser.Expect(TokenType.End, "Unexpected token");

			if (double.IsNaN(result) || double.IsInfinity(result))
			{
				return EvaluationResult.Failure("Math error (overflow or invalid operation)");
			}

			return EvaluationResult.Success(result);
		}
		catch (EvaluationException ex)
		{
			return EvaluationResult.Failure(ex.Message);
		}
	}

	/// <summary>
	/// Formats a numeric value cleanly (integers without decimal point, floats without IEEE-754 noise).
	/// </summary>
	public static string FormatNumber(double value)
	{
		if (double.IsNaN(value) || double.IsInfinity(value))
		{
			return "Error";
		}

		// Snap tiny floating-point inaccuracies near integers
		double roundedInt = Math.Round(value);
		if (Math.Abs(value - roundedInt) < 1e-11 && Math.Abs(roundedInt) < 1e15)
		{
			if (roundedInt == 0.0)
			{
				roundedInt = 0.0; // Avoid "-0"
			}
			return roundedInt.ToString("0", CultureInfo.InvariantCulture);
		}

		double cleaned = Math.Round(value, 10, MidpointRounding.AwayFromZero);
		if (cleaned == 0.0)
		{
			cleaned = 0.0;
		}
		return cleaned.ToString("0.##########", CultureInfo.InvariantCulture);
	}

	private static double ApplyFloor(double value)
	{
		// Guard against IEEE-754 base-2 representation drift such as 0.29 * 100 = 28.999999999999996
		double nearestInt = Math.Round(value);
		if (Math.Abs(value - nearestInt) < 1e-12)
		{
			value = nearestInt;
		}
		return Math.Floor(value);
	}

	private static bool TryTokenize(string input, out List<Token> tokens, out string? error)
	{
		tokens = new List<Token>();
		error = null;
		int i = 0;
		int n = input.Length;

		while (i < n)
		{
			char c = input[i];

			if (char.IsWhiteSpace(c))
			{
				i++;
				continue;
			}

			// Numbers (e.g. 42, 1.5, .5)
			if (char.IsDigit(c) || c == '.')
			{
				int start = i;
				bool hasDot = false;
				bool hasDigit = false;

				while (i < n)
				{
					char ch = input[i];
					if (char.IsDigit(ch))
					{
						hasDigit = true;
						i++;
					}
					else if (ch == '.')
					{
						if (hasDot)
						{
							error = $"Invalid number format at position {i + 1}";
							return false;
						}
						hasDot = true;
						i++;
					}
					else
					{
						break;
					}
				}

				string numStr = input.Substring(start, i - start);
				if (!hasDigit || !double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double numVal))
				{
					error = $"Invalid number '{numStr}'";
					return false;
				}

				tokens.Add(new Token(TokenType.Number, numVal, numStr, start));
				continue;
			}

			// Single-character operators and brackets
			switch (c)
			{
				case '+':
					tokens.Add(new Token(TokenType.Plus, 0, "+", i));
					i++;
					continue;
				case '-':
				case '−': // Unicode minus
					tokens.Add(new Token(TokenType.Minus, 0, "-", i));
					i++;
					continue;
				case '*':
				case '×': // Unicode multiply
					tokens.Add(new Token(TokenType.Multiply, 0, "*", i));
					i++;
					continue;
				case '/':
				case '÷': // Unicode divide
					tokens.Add(new Token(TokenType.Divide, 0, "/", i));
					i++;
					continue;
				case '(':
				case '[':
					tokens.Add(new Token(TokenType.LeftParen, 0, "(", i));
					i++;
					continue;
				case ')':
				case ']':
					tokens.Add(new Token(TokenType.RightParen, 0, ")", i));
					i++;
					continue;
			}

			// Alphabetic identifiers: 'x' (multiply) or 'floor' / 'fl' / 'int'
			if (char.IsLetter(c))
			{
				int start = i;
				while (i < n && char.IsLetter(input[i]))
				{
					i++;
				}

				string word = input.Substring(start, i - start);
				string lower = word.ToLowerInvariant();

				if (lower == "x")
				{
					tokens.Add(new Token(TokenType.Multiply, 0, word, start));
				}
				else if (IsFloorFunctionName(lower))
				{
					tokens.Add(new Token(TokenType.FloorFunc, 0, word, start));
				}
				else if (lower.StartsWith('x') && IsFloorFunctionName(lower.Substring(1)))
				{
					// Support expressions written without spaces like "2xfloor(7x1.5)"
					tokens.Add(new Token(TokenType.Multiply, 0, word.Substring(0, 1), start));
					tokens.Add(new Token(TokenType.FloorFunc, 0, word.Substring(1), start + 1));
				}
				else
				{
					error = $"Unknown symbol or function '{word}'";
					return false;
				}
				continue;
			}

			error = $"Unexpected character '{c}' at position {i + 1}";
			return false;
		}

		tokens.Add(new Token(TokenType.End, 0, string.Empty, n));
		return true;
	}

	private static bool IsFloorFunctionName(string lower) =>
		lower is "floor" or "fl" or "int";

	private sealed class EvaluationException : Exception
	{
		public EvaluationException(string message) : base(message) { }
	}

	private struct Parser
	{
		private readonly List<Token> _tokens;
		private int _pos;

		public Parser(List<Token> tokens)
		{
			_tokens = tokens;
			_pos = 0;
		}

		private Token Current => _tokens[_pos];

		private Token Advance()
		{
			Token current = _tokens[_pos];
			if (_pos < _tokens.Count - 1)
			{
				_pos++;
			}
			return current;
		}

		public void Expect(TokenType expected, string errorPrefix)
		{
			if (Current.Type != expected)
			{
				if (expected == TokenType.RightParen && Current.Type == TokenType.End)
				{
					throw new EvaluationException("Missing closing bracket ')'");
				}
				if (Current.Type == TokenType.RightParen)
				{
					throw new EvaluationException("Unmatched closing bracket ')'");
				}
				throw new EvaluationException($"{errorPrefix} '{Current.Text}'");
			}
			Advance();
		}

		// Expression := Additive
		public double ParseExpression() => ParseAdditive();

		// Additive := Multiplicative ( ('+' | '-') Multiplicative )*
		private double ParseAdditive()
		{
			double left = ParseMultiplicative();

			while (Current.Type is TokenType.Plus or TokenType.Minus)
			{
				TokenType op = Advance().Type;
				double right = ParseMultiplicative();
				left = op == TokenType.Plus ? left + right : left - right;
			}

			return left;
		}

		// Multiplicative := Unary ( ('*' | '/') Unary | ImplicitMultiplication )*
		private double ParseMultiplicative()
		{
			double left = ParseUnary();

			while (true)
			{
				if (Current.Type is TokenType.Multiply or TokenType.Divide)
				{
					TokenType op = Advance().Type;
					double right = ParseUnary();
					if (op == TokenType.Divide)
					{
						if (Math.Abs(right) < 1e-15)
						{
							throw new EvaluationException("Division by zero");
						}
						left /= right;
					}
					else
					{
						left *= right;
					}
				}
				else if (Current.Type is TokenType.LeftParen or TokenType.FloorFunc)
				{
					// Implicit multiplication, e.g., 2(3 + 4) or (2 + 3)(4 + 5) or 2floor(3.5)
					double right = ParseUnary();
					left *= right;
				}
				else
				{
					break;
				}
			}

			return left;
		}

		// Unary := ('+' | '-') Unary | Primary
		private double ParseUnary()
		{
			if (Current.Type == TokenType.Plus)
			{
				Advance();
				return ParseUnary();
			}

			if (Current.Type == TokenType.Minus)
			{
				Advance();
				return -ParseUnary();
			}

			return ParsePrimary();
		}

		// Primary := Number | FloorFunc '(' Expression ')' | '(' Expression ')'
		private double ParsePrimary()
		{
			Token token = Current;

			switch (token.Type)
			{
				case TokenType.Number:
					Advance();
					return token.NumberValue;

				case TokenType.FloorFunc:
				{
					string funcName = Advance().Text;
					if (Current.Type != TokenType.LeftParen)
					{
						throw new EvaluationException($"Expected '(' after '{funcName}'");
					}
					Advance(); // Consume '('
					if (Current.Type == TokenType.RightParen)
					{
						throw new EvaluationException($"Empty argument in '{funcName}()'");
					}
					double inner = ParseExpression();
					Expect(TokenType.RightParen, $"Missing ')' after '{funcName}('");
					return ApplyFloor(inner);
				}

				case TokenType.LeftParen:
				{
					Advance(); // Consume '('
					if (Current.Type == TokenType.RightParen)
					{
						throw new EvaluationException("Empty brackets '()'");
					}
					double inner = ParseExpression();
					Expect(TokenType.RightParen, "Missing closing bracket ')'");
					return inner;
				}

				case TokenType.End:
					throw new EvaluationException("Incomplete expression");

				default:
					throw new EvaluationException($"Unexpected token '{token.Text}'");
			}
		}
	}
}
