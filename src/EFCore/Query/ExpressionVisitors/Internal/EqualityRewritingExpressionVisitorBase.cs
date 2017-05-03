// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public abstract class EqualityRewritingExpressionVisitorBase : ExpressionVisitorBase
    {
        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected QueryCompilationContext QueryCompilationContext { get; }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public EqualityRewritingExpressionVisitorBase(
            [NotNull] QueryCompilationContext queryCompilationContext)
        {
            QueryCompilationContext = queryCompilationContext;
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected virtual Expression CreateKeyComparison(
            IEntityType entityType,
            Expression left,
            Expression right,
            ExpressionType nodeType,
            bool isLeftNullConstant,
            bool isRightNullConstant,
            bool isNullComparison)
        {
            var primaryKeyProperties = entityType.FindPrimaryKey().Properties;

            var newLeftExpression = isLeftNullConstant
                ? Expression.Constant(null, typeof(object))
                : CreateKeyAccessExpression(left, primaryKeyProperties, isNullComparison);

            var newRightExpression = isRightNullConstant
                ? Expression.Constant(null, typeof(object))
                : CreateKeyAccessExpression(right, primaryKeyProperties, isNullComparison);

            return Expression.MakeBinary(nodeType, newLeftExpression, newRightExpression);
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected static Expression CreateKeyAccessExpression(
            Expression target,
            IReadOnlyList<IProperty> properties,
            bool nullComparison)
        {
            // If comparing with null then we need only first PK property
            return properties.Count == 1 || nullComparison
                ? target.CreateEFPropertyExpression(properties[0])
                : Expression.New(
                    AnonymousObject.AnonymousObjectCtor,
                    Expression.NewArrayInit(
                        typeof(object),
                        properties
                            .Select(
                                p => Expression.Convert(
                                    target.CreateEFPropertyExpression(p),
                                    typeof(object)))
                            .Cast<Expression>()
                            .ToArray()));
        }
    }
}
