using Alphaleonis.Vsx;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Editing;
using System.CodeDom.Compiler;
using System.Reflection;
using AlphaVSX.Roslyn;
using Microsoft.CodeAnalysis.CSharp;
using System.Threading;
using System.ComponentModel;
using Alphaleonis.Vsx.Roslyn;
using Alphaleonis.Vsx.Roslyn.CSharp;

namespace Alphaleonis.WcfClientProxyGenerator
{
   public partial class ClientProxyGenerator
   {
      private static class MemberNames
      {
         public const string CloseProxyMethod = "CloseProxy";
         public const string CloseProxyAsyncMethod = "CloseProxyAsync";

         public const string GetProxyMethod = "GetProxy";
         public const string GetProxyAsyncMethod = "GetProxyAsync";

         public const string EnsureProxyMethod = "EnsureProxy";
         public const string EnsureProxyAsyncMethod = "EnsureProxyAsync";

         public const string CachedProxyField = "m_cachedProxy";
         public const string ProxyFactoryField = "m_proxyFactory";

         public const string CancellationTokenParameter = "cancellationToken";
         public const string ProxyVariable = "proxy";

         public const string CreateProxyInstance = "CreateProxyInstance";
         public const string ProxyClass = "ProxyChannel";
      }

      public Task<ClassDeclarationSyntax> GenerateClientClass(SemanticModel semanticModel, SyntaxGenerator gen, INamedTypeSymbol proxyInterface, string name, Accessibility accessibility, bool includeCancellableAsyncMethods, bool suppressWarningComments, MemberAccessibility constructorAccessibility, bool withInternalProxy)
      {
         if (name == null)
         {
            if (proxyInterface.Name.StartsWith("I"))
               name = proxyInterface.Name.Substring(1);

            if (name.EndsWith("Proxy"))
               name = name.Substring(0, name.Length - "Proxy".Length);

            if (!name.EndsWith("Client"))
               name = name + "Client";
         }

         
         SyntaxNode targetClass = gen.ClassDeclaration(name, 
            baseType: gen.TypeExpression(semanticModel.Compilation.RequireType<MarshalByRefObject>()), 
            accessibility: accessibility, 
            modifiers: DeclarationModifiers.Sealed);

         targetClass = gen.AddWarningCommentIf(!suppressWarningComments, targetClass);

         targetClass = gen.AddInterfaceType(targetClass, gen.TypeExpression(semanticModel.Compilation.GetSpecialType(SpecialType.System_IDisposable)));
         targetClass = gen.AddInterfaceType(targetClass, gen.TypeExpression(proxyInterface));

         IEnumerable<IMethodSymbol> methods = GetOperationContractMethods(semanticModel.Compilation, proxyInterface).ToArray();

         GenerationNameTable nameTable = new GenerationNameTable(methods.Select(m => m.Name).Concat(new[] { name }));


         #region Private Fields

         // ==> private IProxy m_cachedProxy;
         SyntaxNode cachedProxyField =
            gen.FieldDeclaration(nameTable[MemberNames.CachedProxyField], gen.TypeExpression(proxyInterface), Accessibility.Private, DeclarationModifiers.None)
            .PrependLeadingTrivia(gen.CreateRegionTrivia("Private Fields"));

         targetClass = gen.AddMembers(targetClass, cachedProxyField);

         // ==> private readonly Func<IProxy> m_proxyFactory;
         SyntaxNode proxyFactoryTypeExpression = gen.TypeExpression(semanticModel.Compilation.RequireTypeByMetadataName("System.Func`1").Construct(proxyInterface));

         targetClass = gen.AddMembers(targetClass, gen.FieldDeclaration(nameTable[MemberNames.ProxyFactoryField], proxyFactoryTypeExpression, Accessibility.Private, DeclarationModifiers.ReadOnly)
            .AddTrailingTrivia(gen.CreateEndRegionTrivia()).AddNewLineTrivia());

         #endregion


         #region Constructors

         // Constructor         
         SyntaxNode constructor = gen.ConstructorDeclaration(
            parameters: new[] { gen.ParameterDeclaration("proxyFactory", proxyFactoryTypeExpression) },
            accessibility: withInternalProxy ? Accessibility.Private : ToAccessibility(constructorAccessibility)
         );

         constructor = gen.AddWarningCommentIf(!suppressWarningComments, constructor);
         constructor = constructor.PrependLeadingTrivia(gen.CreateRegionTrivia("Constructors"));

         constructor = gen.WithStatements(constructor,
            new[]
            {
               // ==> if (proxyFactory == null)
               // ==>   throw new System.ArgumentNullException("proxyFactory");
               gen.ThrowIfNullStatement("proxyFactory"),
               
               // ==> m_proxyFactory = proxyFactory
               gen.AssignmentStatement(
                  gen.MemberAccessExpression(
                     gen.ThisExpression(),
                     gen.IdentifierName(nameTable[MemberNames.ProxyFactoryField])),
                  gen.IdentifierName("proxyFactory")
               )
            }
         ).AddNewLineTrivia();

         if (!withInternalProxy)
            constructor = constructor.AddTrailingTrivia(gen.CreateEndRegionTrivia()).AddNewLineTrivia();

         targetClass = gen.AddMembers(targetClass, constructor);

         ClassDeclarationSyntax proxyClass = null;
         if (withInternalProxy)
         {
            IEnumerable<IMethodSymbol> ctors;
            proxyClass = GenerateProxyClass(semanticModel, gen, proxyInterface, nameTable[MemberNames.ProxyClass], Accessibility.Private, suppressWarningComments, MemberAccessibility.Public, out ctors)
                                                   .PrependLeadingTrivia(gen.CreateRegionTrivia("Proxy Class").Insert(0, gen.NewLine()))
                                                   .AddTrailingTrivia(gen.CreateEndRegionTrivia());

            // Generate one constructor for each of the proxy's constructors.
            foreach (var ctorEntry in ctors.AsSmartEnumerable())
            {
               var ctor = ctorEntry.Value;
               var targetCtor = gen.ConstructorDeclaration(ctor);

               var lambda = gen.ValueReturningLambdaExpression(                  
                  gen.ObjectCreationExpression(gen.IdentifierName(gen.GetName(proxyClass)), ctor.Parameters.Select(p => gen.IdentifierName(p.Name)))
               );

               targetCtor = gen.WithThisConstructorInitializer(targetCtor, new[] { lambda });

               targetCtor = gen.AddWarningCommentIf(!suppressWarningComments, targetCtor);
               targetCtor = gen.WithAccessibility(targetCtor, ToAccessibility(constructorAccessibility));
               
               if (ctorEntry.IsLast)
               {
                  targetCtor = targetCtor.AddTrailingTrivia(gen.CreateEndRegionTrivia()).AddNewLineTrivia();
               }

               targetClass = gen.AddMembers(targetClass, targetCtor.AddNewLineTrivia());
            }


         }

         #endregion

         #region Operation Contract Methods

         // ==> catch
         // ==> {
         // ==>    this.CloseProxy(false);
         // ==>    throw;
         // ==> }
         var catchAndCloseProxyStatement = gen.CatchClause(new SyntaxNode[]
            {
               // ==> this.CloseProxy(false);
               gen.ExpressionStatement(
                  gen.InvocationExpression(
                     gen.MemberAccessExpression(
                        gen.ThisExpression(),
                        nameTable[MemberNames.CloseProxyMethod]
                     ),
                     gen.FalseLiteralExpression()
                  )
               ),

               // throw;
               gen.ThrowStatement()
            });


         foreach (var sourceMethodEntry in methods.AsSmartEnumerable())
         {
            var sourceMethod = sourceMethodEntry.Value;

            using (nameTable.PushScope(sourceMethod.Parameters.Select(p => p.Name)))
            {
               bool isAsync = ReturnsTask(semanticModel.Compilation, sourceMethod);
               bool isVoid = sourceMethod.ReturnType.SpecialType == SpecialType.System_Void || sourceMethod.ReturnType.Equals(semanticModel.Compilation.RequireType<Task>());

               SyntaxNode targetMethod = gen.MethodDeclaration(sourceMethod);

               if (sourceMethodEntry.IsFirst)
                  targetMethod = targetMethod.PrependLeadingTrivia(gen.CreateRegionTrivia("Contract Methods")).AddLeadingTrivia(gen.NewLine());

               targetMethod = gen.AddWarningCommentIf(!suppressWarningComments, targetMethod);

               targetMethod = gen.WithModifiers(targetMethod, isAsync ? DeclarationModifiers.Async : DeclarationModifiers.None);


               targetMethod = gen.WithStatements(targetMethod, new SyntaxNode[]
                  {
                  // ==> try {
                  gen.TryCatchStatement(new SyntaxNode[]
                     {
                        CreateProxyVaraibleDeclaration(gen, nameTable, isAsync),
                        CreateProxyInvocationStatement(semanticModel.Compilation, gen, nameTable, sourceMethod)

                     }, new SyntaxNode[]
                     {
                        catchAndCloseProxyStatement
                     }
                  )
                  });

               targetMethod = targetMethod.AddNewLineTrivia();

               if (sourceMethodEntry.IsLast && !(isAsync && includeCancellableAsyncMethods))
                  targetMethod = targetMethod.AddTrailingTrivia(gen.CreateEndRegionTrivia()).AddNewLineTrivia();

               targetClass = gen.AddMembers(targetClass, targetMethod);

               if (isAsync && includeCancellableAsyncMethods)
               {
                  targetMethod = gen.MethodDeclaration(sourceMethod);
                  targetMethod = gen.AddParameters(targetMethod, new[] { gen.ParameterDeclaration(nameTable[MemberNames.CancellationTokenParameter], gen.TypeExpression(semanticModel.Compilation.RequireType<CancellationToken>())) });
                  targetMethod = gen.WithModifiers(targetMethod, isAsync ? DeclarationModifiers.Async : DeclarationModifiers.None);


                  targetMethod = gen.WithStatements(targetMethod, new SyntaxNode[]
                     {
                     // ==> try {
                     gen.TryCatchStatement(new SyntaxNode[]
                        {
                           CreateProxyVaraibleDeclaration(gen, nameTable, isAsync),
                           CreateCancellableProxyInvocationStatement(semanticModel.Compilation, gen, nameTable, sourceMethod)

                        }, new SyntaxNode[]
                        {
                           catchAndCloseProxyStatement
                        }
                     )
                     });


                  targetMethod = gen.AddWarningCommentIf(!suppressWarningComments, targetMethod.AddNewLineTrivia());

                  if (sourceMethodEntry.IsLast)
                     targetMethod = targetMethod.AddTrailingTrivia(gen.CreateEndRegionTrivia()).AddNewLineTrivia();

                  targetClass = gen.AddMembers(targetClass, targetMethod);
               }
            }
         }

         #endregion

         #region Internal Methods

         targetClass = gen.AddMembers(targetClass, gen.AddWarningCommentIf(!suppressWarningComments, CreateGetProxyMethod(semanticModel.Compilation, gen, proxyInterface, nameTable, false).AddLeadingTrivia(gen.CreateRegionTrivia("Private Methods")).AddNewLineTrivia()));
         targetClass = gen.AddMembers(targetClass, gen.AddWarningCommentIf(!suppressWarningComments, CreateGetProxyMethod(semanticModel.Compilation, gen, proxyInterface, nameTable, true).AddNewLineTrivia()));
         targetClass = gen.AddMembers(targetClass, gen.AddWarningCommentIf(!suppressWarningComments, CreateStaticCloseProxyMethod(semanticModel.Compilation, gen, nameTable, false).AddNewLineTrivia()));
         targetClass = gen.AddMembers(targetClass, gen.AddWarningCommentIf(!suppressWarningComments, CreateStaticCloseProxyMethod(semanticModel.Compilation, gen, nameTable, true).AddNewLineTrivia()));
         targetClass = gen.AddMembers(targetClass, gen.AddWarningCommentIf(!suppressWarningComments, CreateCloseProxyMethod(semanticModel.Compilation, gen, nameTable, false).AddNewLineTrivia()));
         targetClass = gen.AddMembers(targetClass, gen.AddWarningCommentIf(!suppressWarningComments, CreateCloseProxyMethod(semanticModel.Compilation, gen, nameTable, true).AddNewLineTrivia()));
         targetClass = gen.AddMembers(targetClass, gen.AddWarningCommentIf(!suppressWarningComments, CreateEnsureProxyMethod(semanticModel.Compilation, gen, nameTable, false).AddNewLineTrivia()));
         targetClass = gen.AddMembers(targetClass, gen.AddWarningCommentIf(!suppressWarningComments, CreateEnsureProxyMethod(semanticModel.Compilation, gen, nameTable, true).AddTrailingTrivia(gen.CreateEndRegionTrivia()).AddNewLineTrivia()));
         targetClass = gen.AddMembers(targetClass, CreateDisposeMethods(semanticModel.Compilation, gen, nameTable, suppressWarningComments));

         if (withInternalProxy)
         {
            targetClass = gen.AddMembers(targetClass, proxyClass);
         }

         #endregion


         targetClass = AddGeneratedCodeAttribute(gen, targetClass);
         return Task.FromResult((ClassDeclarationSyntax)targetClass);
      }

      private SyntaxNode CreateProxyInvocationStatement(Compilation compilation, SyntaxGenerator g, GenerationNameTable nameTable, IMethodSymbol sourceMethod)
      {
         bool isAsync = ReturnsTask(compilation, sourceMethod);
         bool isVoid = IsVoid(compilation, sourceMethod);

         // proxy.Method(arg1, arg2, ...);
         SyntaxNode invocation = g.InvocationExpression(
                                    g.MemberAccessExpression(
                                       g.IdentifierName(nameTable[MemberNames.ProxyVariable]),
                                       sourceMethod.Name
                                    ),
                                    sourceMethod.Parameters.Select(p => g.IdentifierName(p.Name))
                                 );

         if (isAsync)
         {
            // await proxy.Method(arg1, arg2, ...).ConfigureAwait(false);
            invocation =
               g.AwaitExpression(
                  g.InvocationExpression(
                     g.MemberAccessExpression(
                        invocation,
                        "ConfigureAwait"
                     ),
                     g.FalseLiteralExpression()
                  )
               );
         }

         if (isVoid)
         {
            invocation = g.ExpressionStatement(invocation);
         }
         else
         {
            invocation = g.ReturnStatement(invocation);
         }

         return invocation;

      }

      private SyntaxNode CreateProxyVaraibleDeclaration(SyntaxGenerator g, GenerationNameTable nameTable, bool isAsync)
      {
         // var proxy = this.GetProxy();
         if (!isAsync)
         {
            return g.LocalDeclarationStatement(nameTable[MemberNames.ProxyVariable],
                                             g.InvocationExpression(
                                                g.MemberAccessExpression(
                                                   g.ThisExpression(),
                                                   g.IdentifierName(nameTable[MemberNames.GetProxyMethod])
                                                )
                                             )
                                          );
         }
         else
         {
            // var proxy = await this.GetProxyAsync().ConfigureAwait(false);                                                
            return
               g.LocalDeclarationStatement(nameTable[MemberNames.ProxyVariable],
                  g.AwaitExpression(
                     g.InvocationExpression(
                        g.MemberAccessExpression(
                           g.InvocationExpression(
                              g.MemberAccessExpression(
                                 g.ThisExpression(),
                                 g.IdentifierName(nameTable[MemberNames.GetProxyAsyncMethod])
                              )
                           ), "ConfigureAwait"),
                        g.FalseLiteralExpression()
                     )
                  )
               );
         }
      }

      private SyntaxNode CreateCancellableProxyInvocationStatement(Compilation compilation, SyntaxGenerator g, GenerationNameTable nameTable, IMethodSymbol sourceMethod)
      {
         //using (cancellationToken.Register(s => CloseProxy((System.ServiceModel.ICommunicationObject)s, true), proxy, false))
         string stateVariableName = GetUniqueParameterName("s", sourceMethod);
         return g.UsingStatement(
            g.InvocationExpression(
               g.MemberAccessExpression(
                  g.IdentifierName(nameTable[MemberNames.CancellationTokenParameter]),
                     "Register"
               ),
               g.ValueReturningLambdaExpression(
                  stateVariableName,
                  g.InvocationExpression(
                     g.IdentifierName(nameTable[MemberNames.CloseProxyMethod]),
                     g.CastExpression(
                        compilation.RequireTypeByMetadataName("System.ServiceModel.ICommunicationObject"),
                        g.IdentifierName(stateVariableName)
                     ),
                     g.TrueLiteralExpression()
                  )
               ),
               g.IdentifierName(nameTable[MemberNames.ProxyVariable]),
               g.FalseLiteralExpression()
            ),
            new SyntaxNode[]
            {
               CreateProxyInvocationStatement(compilation, g, nameTable, sourceMethod)
            }
         );
      }

      private static string GetUniqueName(string desiredName, IEnumerable<string> existingNames)
      {
         var result = desiredName;
         int i = 0;
         while (existingNames.Contains(result))
         {
            result = desiredName + (i++);
         }

         return result;
      }

      private static string GetUniqueParameterName(string desiredName, IMethodSymbol method)
      {
         return GetUniqueName(desiredName, method.Parameters.Select(p => p.Name).ToArray());
      }

      private SyntaxNode CreateGetProxyMethod(Compilation compilation, SyntaxGenerator g, INamedTypeSymbol proxyInterface, GenerationNameTable nameTable, bool isAsync)
      {
         //private IProxy GetProxy()
         //{
         //   EnsureProxy();
         //   return m_cachedProxy;
         //}
         return g.MethodDeclaration(
            isAsync ? nameTable[MemberNames.GetProxyAsyncMethod] : nameTable[MemberNames.GetProxyMethod],
            returnType: g.TypeExpression(isAsync ? compilation.RequireType(typeof(Task<>)).Construct(proxyInterface) : proxyInterface),
            accessibility: Accessibility.Private,
            modifiers: isAsync ? DeclarationModifiers.Async : DeclarationModifiers.None,
            statements: new SyntaxNode[]
            {
               g.ExpressionStatement(
                  AwaitExpressionIfAsync(g, isAsync,
                     g.InvocationExpression(
                        g.MemberAccessExpression(
                           g.ThisExpression(),
                           isAsync ? nameTable[MemberNames.EnsureProxyAsyncMethod] : nameTable[MemberNames.EnsureProxyMethod]
                        )
                     )
                  )
               ),
               g.ReturnStatement(
                  g.MemberAccessExpression(
                     g.ThisExpression(),
                     nameTable[MemberNames.CachedProxyField]
                  )
               )
            }
         );
      }

      private SyntaxNode CreateStaticCloseProxyMethod(Compilation compilation, SyntaxGenerator g, GenerationNameTable nameTable, bool asAsync)
      {
         //private static void CloseProxy(System.ServiceModel.ICommunicationObject proxy, bool alwaysAbort)
         //{
         //   try
         //   {
         //      if (proxy != null && proxy.State != System.ServiceModel.CommunicationState.Closed)
         //      {
         //         if (!alwaysAbort && proxy.State != System.ServiceModel.CommunicationState.Faulted)
         //         {
         //            proxy.Close();
         //         }
         //         else
         //         {
         //            proxy.Abort();
         //         }
         //      }
         //   }
         //   catch (System.ServiceModel.CommunicationException)
         //   {
         //      proxy.Abort();
         //   }
         //   catch (System.TimeoutException)
         //   {
         //      proxy.Abort();
         //   }
         //   catch
         //   {
         //      proxy.Abort();
         //      throw;
         //   }
         //}                  
         
         return g.MethodDeclaration(
            asAsync ? nameTable[MemberNames.CloseProxyAsyncMethod] : nameTable[MemberNames.CloseProxyMethod],
            accessibility: Accessibility.Private,
            returnType: asAsync ? g.TypeExpression(compilation.RequireType<Task>()) : null,
            modifiers: (asAsync ? DeclarationModifiers.Async : DeclarationModifiers.None) | DeclarationModifiers.Static,
            parameters: new SyntaxNode[]
            {
               g.ParameterDeclaration("proxy", g.TypeExpression(compilation.RequireTypeByMetadataName("System.ServiceModel.ICommunicationObject"))),
               g.ParameterDeclaration("alwaysAbort", g.TypeExpression(SpecialType.System_Boolean))

            },
            statements: new SyntaxNode[]
            {
               g.TryCatchStatement(
                  new SyntaxNode[]
                  {
                     // if (proxy != null && proxy.State != System.ServiceModel.CommunicationState.Closed)
                     g.IfStatement(
                        g.LogicalAndExpression(
                           g.ReferenceNotEqualsExpression(
                              g.IdentifierName("proxy"),
                              g.NullLiteralExpression()
                           ),
                           g.ValueNotEqualsExpression(
                              g.MemberAccessExpression(
                                 g.IdentifierName("proxy"),
                                 "State"
                              ),
                              g.DottedName("System.ServiceModel.CommunicationState.Closed")
                           )
                        ),
                        new SyntaxNode[]
                        {
                           // if (!alwaysAbort && proxy.State != System.ServiceModel.CommunicationState.Faulted)
                           g.IfStatement(
                              g.LogicalAndExpression(
                                 g.LogicalNotExpression(
                                    g.IdentifierName("alwaysAbort")
                                 ),
                                 g.ValueNotEqualsExpression(
                                    g.MemberAccessExpression(
                                       g.IdentifierName("proxy"),
                                       "State"
                                    ),
                                    g.DottedName("System.ServiceModel.CommunicationState.Faulted")
                                 )
                              ),
                              new SyntaxNode[]
                              {
                                 g.ExpressionStatement(
                                 asAsync ? 
                                 // await System.Threading.Tasks.Task.Factory.FromAsync(proxy.BeginClose, proxy.EndClose, null).ConfigureAwait(false);
                                 AwaitExpression(g,
                                    g.InvocationExpression(
                                       g.DottedName("System.Threading.Tasks.Task.Factory.FromAsync"),
                                       g.DottedName("proxy.BeginClose"),
                                       g.DottedName("proxy.EndClose"),
                                       g.NullLiteralExpression()
                                    )
                                 )
                                 :
                                 // proxy.Close();
                                 g.InvocationExpression(
                                       g.MemberAccessExpression(
                                          g.IdentifierName("proxy"),
                                          "Close"
                                       )
                                    )
                                 )
                              },
                              new SyntaxNode[]
                              {
                                 // proxy.Abort();
                                 g.ExpressionStatement(
                                    g.InvocationExpression(
                                       g.MemberAccessExpression(
                                          g.IdentifierName("proxy"),
                                          "Abort"
                                       )
                                    )
                                 )
                              }
                           )
                        }
                     ),
                  },
                  new SyntaxNode[]
                  {
                     // catch (System.ServiceModel.CommunicationException)
                     g.CatchClause(compilation.RequireTypeByMetadataName("System.ServiceModel.CommunicationException"),
                        new SyntaxNode[]
                        {
                           // proxy.Abort();
                           g.ExpressionStatement(
                              g.InvocationExpression(
                                 g.MemberAccessExpression(
                                    g.IdentifierName("proxy"),
                                    "Abort"
                                 )
                              )
                           )
                        }
                     ),
                     g.CatchClause(compilation.RequireTypeByMetadataName("System.TimeoutException"),
                        new SyntaxNode[]
                        {
                           // proxy.Abort();
                           g.ExpressionStatement(
                              g.InvocationExpression(
                                 g.MemberAccessExpression(
                                    g.IdentifierName("proxy"),
                                    "Abort"
                                 )
                              )
                           )
                        }
                     ),
                     g.CatchClause(
                        new SyntaxNode[]
                        {
                           // proxy.Abort();
                           g.ExpressionStatement(
                              g.InvocationExpression(
                                 g.MemberAccessExpression(
                                    g.IdentifierName("proxy"),
                                    "Abort"
                                 )
                              )
                           ),
                           g.ThrowStatement()
                        }
                     )

                  }
               )
            }
         );
      }

      private SyntaxNode CreateCloseProxyMethod(Compilation compilation, SyntaxGenerator g, GenerationNameTable nameTable, bool asAsync)
      {
         return
            g.MethodDeclaration(
               asAsync ? nameTable[MemberNames.CloseProxyAsyncMethod] : nameTable[MemberNames.CloseProxyMethod],
               returnType: asAsync ? g.TypeExpression(compilation.RequireType<Task>()) : null,
               parameters: new SyntaxNode[] { g.ParameterDeclaration("alwaysAbort", g.TypeExpression(SpecialType.System_Boolean)) },
               modifiers: asAsync ? DeclarationModifiers.Async : DeclarationModifiers.None,
               accessibility: Accessibility.Private,
               statements: new SyntaxNode[]
               {
                  g.IfStatement(
                     g.ReferenceNotEqualsExpression(
                        g.IdentifierName(nameTable[MemberNames.CachedProxyField]),
                        g.NullLiteralExpression()
                     ),
                     new SyntaxNode[]
                     {
                        g.LocalDeclarationStatement(
                           "proxy",
                           g.TryCastExpression(
                              g.MemberAccessExpression(
                                 g.ThisExpression(),
                                 nameTable[MemberNames.CachedProxyField]
                              ),
                              compilation.RequireTypeByMetadataName("System.ServiceModel.ICommunicationObject")
                           )
                        ),
                        g.TryFinallyStatement(
                           new SyntaxNode[]
                           {
                              AwaitExpressionIfAsync(g, asAsync,
                                 g.InvocationExpression(
                                    g.IdentifierName(asAsync ? nameTable[MemberNames.CloseProxyAsyncMethod] : nameTable[MemberNames.CloseProxyMethod]),
                                    g.IdentifierName("proxy"),
                                    g.IdentifierName("alwaysAbort")
                                 )
                              )
                           },
                           new SyntaxNode[]
                           {
                              g.AssignmentStatement(
                                 g.MemberAccessExpression(
                                    g.ThisExpression(),
                                    nameTable[MemberNames.CachedProxyField]
                                 ),
                                 g.NullLiteralExpression()
                              )
                           }
                        )
                     }
                  )
               }
            );
      }

      private SyntaxNode CreateEnsureProxyMethod(Compilation compilation, SyntaxGenerator g, GenerationNameTable nameTable, bool asAsync)
      {
         /*
            private async System.Threading.Tasks.Task EnsureProxyAsync()
            {
               if (m_cachedProxy != null && (
                   ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Faulted ||
                   ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Closed))
               {
                  await CloseProxyAsync().ConfigureAwait(false);
               }

               if (m_cachedProxy == null)
               {
                  var proxy = m_proxyFactory();
                  await System.Threading.Tasks.Task.Factory.FromAsync(((System.ServiceModel.ICommunicationObject)proxy).BeginOpen, ((System.ServiceModel.ICommunicationObject)proxy).EndOpen, null).ConfigureAwait(false);
                  m_cachedProxy = proxy;
               }
            }
         */
         return
            g.MethodDeclaration(
               asAsync ? nameTable[MemberNames.EnsureProxyAsyncMethod] : nameTable[MemberNames.EnsureProxyMethod],
               returnType: asAsync ? g.TypeExpression(compilation.RequireType<Task>()) : null,
               accessibility: Accessibility.Private,
               modifiers: asAsync ? DeclarationModifiers.Async : DeclarationModifiers.None,
               statements: new SyntaxNode[]
               {
                  //if (m_cachedProxy != null && (
                  //             ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Faulted ||
                  //             ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Closed))
                  g.IfStatement(
                     g.LogicalAndExpression(
                        // m_cachedProxy != null
                        g.ReferenceNotEqualsExpression(
                           g.MemberAccessExpression(
                              g.ThisExpression(),
                              nameTable[MemberNames.CachedProxyField]
                           ),
                           g.NullLiteralExpression()
                        ),
                        g.LogicalOrExpression(
                           // ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Faulted
                           g.ValueEqualsExpression(
                              g.MemberAccessExpression(
                                 g.CastExpression(
                                    compilation.RequireTypeByMetadataName("System.ServiceModel.ICommunicationObject"),
                                    g.MemberAccessExpression(
                                       g.ThisExpression(),
                                       nameTable[MemberNames.CachedProxyField]
                                    )
                                 ),
                                 "State"
                              ),
                              g.DottedName("System.ServiceModel.CommunicationState.Faulted")
                           ),
                           // ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Faulted
                           g.ValueEqualsExpression(
                              g.MemberAccessExpression(
                                 g.CastExpression(
                                    compilation.RequireTypeByMetadataName("System.ServiceModel.ICommunicationObject"),
                                    g.MemberAccessExpression(
                                       g.ThisExpression(),
                                       nameTable[MemberNames.CachedProxyField]
                                    )
                                 ),
                                 "State"
                              ),
                              g.DottedName("System.ServiceModel.CommunicationState.Closed")
                           )
                        )
                     ),
                     new SyntaxNode[]
                     {
                        // await CloseProxyAsync(false).ConfigureAwait(false);
                        // or
                        // CloseProxy(false);
                        AwaitExpressionIfAsync(g, asAsync,
                           g.InvocationExpression(
                              g.MemberAccessExpression(
                                 g.ThisExpression(),
                                 asAsync ? nameTable[MemberNames.CloseProxyAsyncMethod] : nameTable[MemberNames.CloseProxyMethod]
                              ),
                              g.FalseLiteralExpression()
                           )
                        )
                     }
                  ),
                  g.IfStatement(
                     g.ReferenceEqualsExpression(
                        g.MemberAccessExpression(
                           g.ThisExpression(),
                           nameTable[MemberNames.CachedProxyField]
                        ),
                        g.NullLiteralExpression()
                     ),
                     new SyntaxNode[]
                     {
                        g.LocalDeclarationStatement(
                           "proxy",
                           g.InvocationExpression(
                              g.MemberAccessExpression(
                                 g.ThisExpression(),
                                 nameTable[MemberNames.ProxyFactoryField]
                              )
                           )
                        ),
                        g.ExpressionStatement(
                           asAsync ?
                           AwaitExpression(g,
                                    g.InvocationExpression(
                                       g.DottedName("System.Threading.Tasks.Task.Factory.FromAsync"),
                                       g.MemberAccessExpression(
                                          g.CastExpression(
                                              compilation.RequireTypeByMetadataName("System.ServiceModel.ICommunicationObject"),
                                              g.IdentifierName("proxy")
                                          ),
                                          "BeginOpen"
                                       ),
                                       g.MemberAccessExpression(
                                          g.CastExpression(
                                              compilation.RequireTypeByMetadataName("System.ServiceModel.ICommunicationObject"),
                                              g.IdentifierName("proxy")
                                          ),
                                          "EndOpen"
                                       ),
                                       g.NullLiteralExpression()
                                    )
                                 )
                           :
                           g.InvocationExpression(
                              g.MemberAccessExpression(
                                 g.CastExpression(
                                    compilation.RequireTypeByMetadataName("System.ServiceModel.ICommunicationObject"),
                                    g.IdentifierName("proxy")
                                 ),
                                 "Open"
                              )
                           )
                        ),
                        g.AssignmentStatement(
                           g.MemberAccessExpression(
                              g.ThisExpression(),
                              nameTable[MemberNames.CachedProxyField]
                           ),
                           g.IdentifierName("proxy")
                        )
                     }
                  )
               }
            );

      }

      private IEnumerable<SyntaxNode> CreateDisposeMethods(Compilation compilation, SyntaxGenerator g, GenerationNameTable nameTable, bool suppressWarningComments)
      {
         yield return g.AddWarningCommentIf(!suppressWarningComments,
         g.MethodDeclaration(
            "Dispose",
            accessibility: Accessibility.Public,
            statements: new SyntaxNode[]
            {
               g.InvocationExpression(
                  g.MemberAccessExpression(
                     g.ThisExpression(),
                     "Dispose"
                  ),
                  g.TrueLiteralExpression()
               ),
               g.InvocationExpression(
                  g.MemberAccessExpression(
                     g.TypeExpression(compilation.RequireTypeByMetadataName("System.GC")),
                     "SuppressFinalize"
                  ),
                  g.ThisExpression()
               )
            }
         ))
         .PrependLeadingTrivia(g.CreateRegionTrivia("IDisposable"));


         yield return g.AddWarningCommentIf(!suppressWarningComments, g.MethodDeclaration(
            "Dispose",
            parameters: new SyntaxNode[] { g.ParameterDeclaration("disposing", g.TypeExpression(SpecialType.System_Boolean)) },
            accessibility: Accessibility.Private,
            statements: new SyntaxNode[]
            {
               g.IfStatement(
                  g.ValueEqualsExpression(
                     g.IdentifierName("disposing"),
                     g.TrueLiteralExpression()
                  ),
                  new SyntaxNode[]
                  {
                     g.TryCatchStatement(
                        new SyntaxNode[]
                        {
                           g.ExpressionStatement(
                              g.InvocationExpression(
                                 g.MemberAccessExpression(
                                    g.ThisExpression(),
                                    nameTable[MemberNames.CloseProxyMethod]
                                 ),
                                 g.FalseLiteralExpression()
                              )
                           )
                        },
                        new SyntaxNode[]
                        {
                           g.CatchClause(new SyntaxNode [0])
                        }
                     )
                  }
               )
            }
         )).AddTrailingTrivia(g.CreateEndRegionTrivia());
      }
   }
}




