using System.Linq.Expressions;
using System.Reflection;

namespace Haldus;

/// <summary>
/// Arama koşullarını sorgu ifadelerine çevirir. Üretilen kod her alan için buradaki bir metodu çağırır;
/// hangi alanın aranabileceğine üretilen kod, koşulun nasıl uygulanacağına bu sınıf karar verir.
/// </summary>
public static class HaldusSearch
{
    private static readonly MethodInfo ToLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
    private static readonly MethodInfo StringContains = typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo StringStartsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo StringEndsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;

    public static Expression<Func<T, bool>>? String<T>(
        Expression<Func<T, string?>> selector,
        StringCondition? condition,
        SearchBudget budget)
    {
        if (condition is null)
        {
            return null;
        }

        var member = selector.Body;
        var target = condition.IgnoreCase ? Expression.Call(member, ToLower) : member;
        var parts = new List<Expression>();

        string Normalize(string value) => condition.IgnoreCase ? value.ToLowerInvariant() : value;

        if (condition.Eq is { } eq)
        {
            parts.Add(Expression.Equal(target, Parameter(Normalize(eq))));
        }

        if (condition.Ne is { } ne)
        {
            parts.Add(Expression.NotEqual(target, Parameter(Normalize(ne))));
        }

        if (condition.Contains is { } contains)
        {
            parts.Add(Expression.Call(target, StringContains, Parameter(Normalize(contains))));
        }

        if (condition.StartsWith is { } startsWith)
        {
            parts.Add(Expression.Call(target, StringStartsWith, Parameter(Normalize(startsWith))));
        }

        if (condition.EndsWith is { } endsWith)
        {
            parts.Add(Expression.Call(target, StringEndsWith, Parameter(Normalize(endsWith))));
        }

        if (condition.In is { } values)
        {
            budget.CountListValues(values.Count);
            parts.Add(Contains(values.Select(Normalize).ToList(), target));
        }

        if (condition.IsNull is { } isNull)
        {
            parts.Add(NullCheck(member, isNull));
        }

        return Combine<T>(selector.Parameters[0], parts, budget);
    }

    public static Expression<Func<T, bool>>? Equality<T, TValue>(
        Expression<Func<T, TValue>> selector,
        EqualityCondition<TValue>? condition,
        SearchBudget budget)
        where TValue : struct =>
        condition is null
            ? null
            : Compare<T, TValue>(selector, condition.Eq, condition.Ne, null, null, null, null, condition.In, condition.IsNull, budget);

    public static Expression<Func<T, bool>>? NullableEquality<T, TValue>(
        Expression<Func<T, TValue?>> selector,
        EqualityCondition<TValue>? condition,
        SearchBudget budget)
        where TValue : struct =>
        condition is null
            ? null
            : Compare<T, TValue>(selector, condition.Eq, condition.Ne, null, null, null, null, condition.In, condition.IsNull, budget);

    public static Expression<Func<T, bool>>? Range<T, TValue>(
        Expression<Func<T, TValue>> selector,
        RangeCondition<TValue>? condition,
        SearchBudget budget)
        where TValue : struct =>
        condition is null
            ? null
            : Compare<T, TValue>(
                selector, condition.Eq, condition.Ne, condition.Gt, condition.Gte, condition.Lt, condition.Lte,
                condition.In, condition.IsNull, budget);

    public static Expression<Func<T, bool>>? NullableRange<T, TValue>(
        Expression<Func<T, TValue?>> selector,
        RangeCondition<TValue>? condition,
        SearchBudget budget)
        where TValue : struct =>
        condition is null
            ? null
            : Compare<T, TValue>(
                selector, condition.Eq, condition.Ne, condition.Gt, condition.Gte, condition.Lt, condition.Lte,
                condition.In, condition.IsNull, budget);

    /// <summary>Verilen koşulları VE ile birleştirir; hiçbiri yoksa <c>null</c> döner.</summary>
    public static Expression<Func<T, bool>>? All<T>(IEnumerable<Expression<Func<T, bool>>?>? predicates) =>
        Merge(predicates, Expression.AndAlso);

    /// <summary>Verilen koşulları VEYA ile birleştirir; hiçbiri yoksa <c>null</c> döner.</summary>
    public static Expression<Func<T, bool>>? Any<T>(IEnumerable<Expression<Func<T, bool>>?>? predicates) =>
        Merge(predicates, Expression.OrElse);

    public static Expression<Func<T, bool>>? Not<T>(Expression<Func<T, bool>>? predicate) =>
        predicate is null ? null : Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);

    /// <summary>İlk sıralama alanını <c>OrderBy</c>, sonrakileri <c>ThenBy</c> olarak ekler.</summary>
    public static IOrderedQueryable<T> OrderBy<T, TKey>(
        IQueryable<T> source,
        IOrderedQueryable<T>? ordered,
        Expression<Func<T, TKey>> key,
        bool descending) =>
        (ordered, descending) switch
        {
            (null, false) => source.OrderBy(key),
            (null, true) => source.OrderByDescending(key),
            (not null, false) => ordered.ThenBy(key),
            (not null, true) => ordered.ThenByDescending(key),
        };

    private static Expression<Func<T, bool>>? Compare<T, TValue>(
        LambdaExpression selector,
        TValue? eq,
        TValue? ne,
        TValue? gt,
        TValue? gte,
        TValue? lt,
        TValue? lte,
        IReadOnlyList<TValue>? values,
        bool? isNull,
        SearchBudget budget)
        where TValue : struct
    {
        var member = selector.Body;
        var nullable = member.Type != typeof(TValue);
        var parts = new List<Expression>();

        // Alan nullable ise karşılaştırılan değer de aynı tipe çevrilir.
        Expression Value(TValue value) => nullable ? Expression.Convert(Parameter(value), member.Type) : Parameter(value);

        if (eq is { } eqValue)
        {
            parts.Add(Expression.Equal(member, Value(eqValue)));
        }

        if (ne is { } neValue)
        {
            parts.Add(Expression.NotEqual(member, Value(neValue)));
        }

        if (gt is { } gtValue)
        {
            parts.Add(Expression.GreaterThan(member, Value(gtValue)));
        }

        if (gte is { } gteValue)
        {
            parts.Add(Expression.GreaterThanOrEqual(member, Value(gteValue)));
        }

        if (lt is { } ltValue)
        {
            parts.Add(Expression.LessThan(member, Value(ltValue)));
        }

        if (lte is { } lteValue)
        {
            parts.Add(Expression.LessThanOrEqual(member, Value(lteValue)));
        }

        if (values is not null)
        {
            budget.CountListValues(values.Count);
            parts.Add(nullable
                ? Contains(values.Select(value => (TValue?)value).ToList(), member)
                : Contains(values.ToList(), member));
        }

        if (isNull is { } isNullValue)
        {
            // Null olamayan alan hiçbir zaman null değildir.
            parts.Add(nullable ? NullCheck(member, isNullValue) : Expression.Constant(!isNullValue));
        }

        return Combine<T>(selector.Parameters[0], parts, budget);
    }

    private static Expression<Func<T, bool>>? Combine<T>(ParameterExpression parameter, List<Expression> parts, SearchBudget budget)
    {
        budget.CountConditions(parts.Count);

        return parts.Count == 0
            ? null
            : Expression.Lambda<Func<T, bool>>(parts.Aggregate(Expression.AndAlso), parameter);
    }

    private static Expression<Func<T, bool>>? Merge<T>(
        IEnumerable<Expression<Func<T, bool>>?>? predicates,
        Func<Expression, Expression, BinaryExpression> combine)
    {
        if (predicates is null)
        {
            return null;
        }

        // Her koşul kendi parametresiyle gelir; birleştirmeden önce hepsi tek parametreye bağlanır.
        var parameter = Expression.Parameter(typeof(T), "item");
        Expression? body = null;

        foreach (var predicate in predicates)
        {
            if (predicate is null)
            {
                continue;
            }

            var rebound = new ParameterReplacer(predicate.Parameters[0], parameter).Visit(predicate.Body);
            body = body is null ? rebound : combine(body, rebound);
        }

        return body is null ? null : Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private static BinaryExpression NullCheck(Expression member, bool isNull)
    {
        var nothing = Expression.Constant(null, member.Type);
        return isNull ? Expression.Equal(member, nothing) : Expression.NotEqual(member, nothing);
    }

    private static MethodCallExpression Contains<TItem>(List<TItem> values, Expression item) =>
        Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(TItem)], Parameter(values), item);

    /// <summary>
    /// Değeri sabit olarak değil, bir nesnenin özelliği olarak ifadeye koyar. EF Core böyle ifadeleri SQL
    /// parametresine çevirir; değer sorgu metnine gömülmez ve sorgu planı yeniden kullanılır.
    /// </summary>
    private static MemberExpression Parameter<TValue>(TValue value) =>
        Expression.Property(Expression.Constant(new Holder<TValue>(value)), nameof(Holder<TValue>.Value));

    private sealed class Holder<TValue>(TValue value)
    {
        public TValue Value { get; } = value;
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
