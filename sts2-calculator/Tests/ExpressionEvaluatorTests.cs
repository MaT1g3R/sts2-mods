using Sts2Calculator.Core;
using Xunit;

namespace Sts2Calculator.Tests;

public class ExpressionEvaluatorTests
{
	[Theory]
	[InlineData("2 * 4 + 8", "16")]
	[InlineData("8 + 2 * 4", "16")]
	[InlineData("(2 + 4) * 8", "48")]
	[InlineData("2 * (4 + 8)", "24")]
	[InlineData("2 x 4 + 8", "16")]
	[InlineData("2x4+8", "16")]
	[InlineData("2 X 4 + 8", "16")]
	[InlineData("10 - 3 - 2", "5")]
	[InlineData("20 / 5 * 2", "8")]
	[InlineData("20 / (5 * 2)", "2")]
	[InlineData("7 * 1.5", "10.5")]
	[InlineData("7 x 1.5", "10.5")]
	[InlineData("7x1.5", "10.5")]
	[InlineData("-5 + 12", "7")]
	[InlineData("2 * -4 + 10", "2")]
	[InlineData("-(2 + 3) * 4", "-20")]
	public void Evaluate_OrderOfOperationsAndOperators_ReturnsExpected(string expr, string expected)
	{
		EvaluationResult result = ExpressionEvaluator.Evaluate(expr);
		Assert.True(result.IsSuccess, $"Expected success for '{expr}', got error: {result.ErrorMessage}");
		Assert.Equal(expected, result.FormattedValue);
	}

	[Theory]
	[InlineData("floor(7 x 1.5)", "10")]
	[InlineData("fl(7 x 1.5)", "10")]
	[InlineData("int(7 x 1.5)", "10")]
	[InlineData("floor(7 * 1.5)", "10")]
	[InlineData("fl(7 * 1.5)", "10")]
	[InlineData("int(7 * 1.5)", "10")]
	[InlineData("FLOOR(7 x 1.5)", "10")]
	[InlineData("FL(7 x 1.5)", "10")]
	[InlineData("INT(7 x 1.5)", "10")]
	[InlineData("floor(7x1.5)", "10")]
	[InlineData("int(7x1.5)", "10")]
	[InlineData("int(10x0.75)", "7")]
	[InlineData("int(10x1.75)", "17")]
	[InlineData("2 * floor(7 x 1.5) + 5", "25")]
	[InlineData("2xfloor(7x1.5)", "20")]
	[InlineData("floor(7 x 1.5) + fl(3.9) * int(2.8)", "16")]
	[InlineData("floor((6 + 5) * 0.75)", "8")]
	[InlineData("floor(0.29 * 100)", "29")]
	[InlineData("floor(-1.2)", "-2")]
	public void Evaluate_FloorFunctions_ReturnsExpected(string expr, string expected)
	{
		EvaluationResult result = ExpressionEvaluator.Evaluate(expr);
		Assert.True(result.IsSuccess, $"Expected success for '{expr}', got error: {result.ErrorMessage}");
		Assert.Equal(expected, result.FormattedValue);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("2 +")]
	[InlineData("floor()")]
	[InlineData("floor(7 * 1.5")]
	[InlineData("(2 + 3))")]
	[InlineData("10 / 0")]
	[InlineData("foo(5)")]
	public void Evaluate_InvalidExpressions_ReturnsFailure(string expr)
	{
		EvaluationResult result = ExpressionEvaluator.Evaluate(expr);
		Assert.False(result.IsSuccess);
		Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
	}
}
