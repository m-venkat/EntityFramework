// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Extensions.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Remotion.Linq.Clauses.Expressions;
using Microsoft.EntityFrameworkCore.Internal;
using Microsoft.EntityFrameworkCore.Utilities;
using System.Reflection;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class CollectionNavigationEqualityRewritingExpressionVisitor : EqualityRewritingExpressionVisitorBase
    {
        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public CollectionNavigationEqualityRewritingExpressionVisitor(
            [NotNull] QueryCompilationContext queryCompilationContext)
            : base(queryCompilationContext)
        {
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitBinary(BinaryExpression binaryExpression)
        {
            Check.NotNull(binaryExpression, nameof(binaryExpression));

            var newBinaryExpression = (BinaryExpression)base.VisitBinary(binaryExpression);

            if (newBinaryExpression.NodeType == ExpressionType.Equal
                || newBinaryExpression.NodeType == ExpressionType.NotEqual)
            {
                var isLeftNullConstant = newBinaryExpression.Left.IsNullConstantExpression();
                var isRightNullConstant = newBinaryExpression.Right.IsNullConstantExpression();

                if (isLeftNullConstant && isRightNullConstant)
                {
                    return newBinaryExpression;
                }

                QuerySourceReferenceExpression leftNavigationQsre;
                QuerySourceReferenceExpression rightNavigationQsre;

                var leftProperties = MemberAccessBindingExpressionVisitor.GetPropertyPath(
                    newBinaryExpression.Left, QueryCompilationContext, out leftNavigationQsre);

                var rightProperties = MemberAccessBindingExpressionVisitor.GetPropertyPath(
                    newBinaryExpression.Right, QueryCompilationContext, out rightNavigationQsre);

                var isNullComparison = isLeftNullConstant || isRightNullConstant;
                var nonNullExpression = isLeftNullConstant ? newBinaryExpression.Right : newBinaryExpression.Left;

                if (isNullComparison)
                {
                    var nonNullNavigationQsre = isLeftNullConstant ? rightNavigationQsre : leftNavigationQsre;
                    var nonNullproperties = isLeftNullConstant ? rightProperties : leftProperties;

                    if (IsCollectionNavigation(nonNullNavigationQsre, nonNullproperties))
                    {
                        // collection navigation is only null if its parent entity is null (null propagation thru navigation)
                        // we can rewrite c.Orders == null into c == null, which in turn can be rewriteen into c.Id == null
                        // it is probable that user wanted to see if the collection is (not) empty, log warning suggesting to use Any() instead.
                        QueryCompilationContext.Logger.PossibleUnintendedCollectionNavigationNullComparisonWarning(
                            string.Join(".", nonNullproperties.Select(p => p.Name)));

                        var callerExpression = CreateCollectionCallerExpression(nonNullNavigationQsre, nonNullproperties);

                        return Expression.MakeBinary(newBinaryExpression.NodeType, callerExpression, Expression.Constant(null));
                    }
                }

                var collectionNavigationComparison = TryRewriteCollectionNavigationComparison(
                    newBinaryExpression.Left,
                    newBinaryExpression.Right,
                    newBinaryExpression.NodeType,
                    leftNavigationQsre,
                    rightNavigationQsre,
                    leftProperties,
                    rightProperties);

                if (collectionNavigationComparison != null)
                {
                    return collectionNavigationComparison;
                }
            }

            return newBinaryExpression;
        }

        private readonly MethodInfo _objectEqualsMethodInfo
            = typeof(object).GetRuntimeMethod(nameof(object.Equals), new[] { typeof(object), typeof(object) });

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
        {
            Expression leftExpression = null;
            Expression rightExpression = null;
            if (methodCallExpression.Method.Name == nameof(object.Equals)
                && methodCallExpression.Arguments.Count == 1
                && methodCallExpression.Object != null)
            {
                leftExpression = methodCallExpression.Object;
                rightExpression = methodCallExpression.Arguments[0];
            }

            if (methodCallExpression.Method.Equals(_objectEqualsMethodInfo))
            {
                leftExpression = methodCallExpression.Arguments[0];
                rightExpression = methodCallExpression.Arguments[1];
            }

            //var objectRootEntityType = QueryCompilationContext.Model.FindEntityType(objectExpression.Type)?.RootType();
            //var argumentRootEntityType = QueryCompilationContext.Model.FindEntityType(argumentExpression.Type)?.RootType();
            //if (objectRootEntityType == argumentRootEntityType)
            //{
            //    var newExpression = Expression.Equal(objectExpression, argumentExpression);

            //    return Visit(newExpression);
            //}

            if (leftExpression != null && rightExpression != null)
            {
                QuerySourceReferenceExpression leftNavigationQsre;
                QuerySourceReferenceExpression rightNavigationQsre;

                var leftProperties = MemberAccessBindingExpressionVisitor.GetPropertyPath(
                    leftExpression, QueryCompilationContext, out leftNavigationQsre);

                var rightProperties = MemberAccessBindingExpressionVisitor.GetPropertyPath(
                    rightExpression, QueryCompilationContext, out rightNavigationQsre);

                var collectionNavigationComparison = TryRewriteCollectionNavigationComparison(
                    leftExpression,
                    rightExpression,
                    ExpressionType.Equal,
                    leftNavigationQsre,
                    rightNavigationQsre,
                    leftProperties,
                    rightProperties);

                if (collectionNavigationComparison != null)
                {
                    return collectionNavigationComparison;
                }

                // TODO: null comparison
            }

            return base.VisitMethodCall(methodCallExpression);
        }

        private Expression TryRewriteCollectionNavigationComparison(
             Expression leftExpressionn,
             Expression rightExpressionn,
             ExpressionType expressionType,
             QuerySourceReferenceExpression leftNavigationQsre,
             QuerySourceReferenceExpression rightNavigationQsre,
             IList<IPropertyBase> leftProperties,
             IList<IPropertyBase> rightProperties)
        {
            // collection compared to another collection
            // if both collections are the same navigations, compare their parent entities (by key)
            // otherwise we assume they are different references and return false
            if (IsCollectionNavigation(leftNavigationQsre, leftProperties)
                && IsCollectionNavigation(rightNavigationQsre, rightProperties))
            {
                QueryCompilationContext.Logger.PossibleUnintendedReferenceComparison(leftExpressionn, rightExpressionn);

                if (leftProperties[leftProperties.Count - 1].Equals(rightProperties[rightProperties.Count - 1]))
                {
                    var newLeft = CreateCollectionCallerExpression(leftNavigationQsre, leftProperties);
                    var newRight = CreateCollectionCallerExpression(rightNavigationQsre, rightProperties);

                    return CreateKeyComparison(
                        ((INavigation)leftProperties[leftProperties.Count - 1]).DeclaringEntityType,
                        newLeft,
                        newRight,
                        expressionType,
                        isLeftNullConstant: false,
                        isRightNullConstant: false,
                        isNullComparison: false);
                }

                return Expression.Constant(false);
            }

            return null;
        }

        private Expression CreateCollectionCallerExpression(
            QuerySourceReferenceExpression qsre,
            IList<IPropertyBase> properties)
        {
            var result = (Expression)qsre;
            for (var i = 0; i < properties.Count - 1; i++)
            {
                result = result.CreateEFPropertyExpression(properties[i], makeNullable: false);
            }

            return result;
        }

        private static bool IsCollectionNavigation(QuerySourceReferenceExpression qsre, IList<IPropertyBase> properties)
            => qsre != null
                && properties.Count > 0
                && properties[properties.Count - 1] is INavigation navigation
                && navigation.IsCollection();
    }
}
