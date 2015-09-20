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


namespace Alphaleonis.WcfClientProxyGenerator
{
   public partial class ClientProxyGenerator
   {
      private class ClientGenerationNameTable
      {
         public ClientGenerationNameTable(IEnumerable<string> existingMembers)
         {
            HashSet<string> names = new HashSet<string>(existingMembers);
            CloseProxyMethodName = GetUniqueName("CloseProxy", names);
            CloseProxyAsyncMethodName = GetUniqueName("CloseProxyAsync", names);
            GetProxyAsyncMethodName = GetUniqueName("GetProxyAsync", names);
            GetProxyMethodName = GetUniqueName("GetProxy", names);
            EnsureProxyMethodName = GetUniqueName("EnsureProxy", names);
            EnsureProxyAsyncMethodName = GetUniqueName("EnsureProxyAsync", names);
            CachedProxyFieldName = GetUniqueName("m_cachedProxy", names);
            ProxyFactoryFieldName = GetUniqueName("m_proxyFactory", names);
         }


         public string CloseProxyMethodName { get; }
         public string CloseProxyAsyncMethodName { get; }
         public string GetProxyAsyncMethodName { get; }
         public string GetProxyMethodName { get; }
         public string EnsureProxyMethodName { get; }
         public string EnsureProxyAsyncMethodName { get; }
         public string CachedProxyFieldName { get; }
         public string ProxyFactoryFieldName { get; }

         public string CancellationTokenParameterName { get; private set; }
         public string ProxyVariableName { get; private set; }
         

         public void ResetForMethod(IMethodSymbol method)
         {
            CancellationTokenParameterName = GetUniqueParameterName("cancellationToken", method);
            ProxyVariableName = GetUniqueParameterName("proxy", method);
         }
      }

      public ClassDeclarationSyntax GenerateClientClass(CSharpRoslynCodeGenerationContext context, INamedTypeSymbol proxyInterface, string name = null, Accessibility accessibility = Accessibility.Public, bool includeCancellableAsyncMethods = true)
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

         SyntaxGenerator gen = context.Generator;
         SyntaxNode targetClass = gen.ClassDeclaration(name, baseType: gen.TypeExpression(RequireTypeSymbol<MarshalByRefObject>(context)), accessibility: accessibility, modifiers: DeclarationModifiers.Sealed);
         targetClass = gen.AddInterfaceType(targetClass, gen.TypeExpression(context.Compilation.GetSpecialType(SpecialType.System_IDisposable)));
         targetClass = gen.AddInterfaceType(targetClass, gen.TypeExpression(proxyInterface));

         IEnumerable<IMethodSymbol> methods = GetOperationContractMethods(context, proxyInterface).ToArray();

         ClientGenerationNameTable nameTable = new ClientGenerationNameTable(methods.Select(m => m.Name));


         #region Private Fields

         // ==> private IProxy m_cachedProxy;
         targetClass = gen.AddMembers(targetClass, gen.FieldDeclaration(nameTable.CachedProxyFieldName, gen.TypeExpression(proxyInterface), Accessibility.Private, DeclarationModifiers.None));

         // ==> private readonly Func<IProxy> m_proxyFactory;
         SyntaxNode proxyFactoryTypeExpression = gen.TypeExpression(context.Compilation.GetTypeByMetadataName("System.Func`1").Construct(proxyInterface));
         targetClass = gen.AddMembers(targetClass, gen.FieldDeclaration(nameTable.ProxyFactoryFieldName, proxyFactoryTypeExpression, Accessibility.Private, DeclarationModifiers.ReadOnly));

         #endregion

         #region Constructor

         // Constructor         
         SyntaxNode constructor = gen.ConstructorDeclaration(parameters: new[] { gen.ParameterDeclaration("proxyFactory", proxyFactoryTypeExpression) }, accessibility: Accessibility.Public);

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
                     gen.IdentifierName(nameTable.ProxyFactoryFieldName)),
                  gen.IdentifierName("proxyFactory")
               )
            }
         );

         targetClass = gen.AddMembers(targetClass, constructor);

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
                        nameTable.CloseProxyMethodName
                     ),
                     gen.FalseLiteralExpression()
                  )
               ),

               // throw;
               gen.ThrowStatement()
            });

         foreach (IMethodSymbol sourceMethod in methods)
         {
            nameTable.ResetForMethod(sourceMethod);

            bool isAsync = ReturnsTask(context, sourceMethod);
            bool isVoid = sourceMethod.ReturnType.SpecialType == SpecialType.System_Void || sourceMethod.ReturnType.Equals(GetVoidTaskType(context));

            SyntaxNode targetMethod = gen.MethodDeclaration(sourceMethod);
            targetMethod = gen.WithModifiers(targetMethod, isAsync ? DeclarationModifiers.Async : DeclarationModifiers.None);


            targetMethod = gen.WithStatements(targetMethod, new SyntaxNode[]
               {
                  // ==> try {
                  gen.TryCatchStatement(new SyntaxNode[]
                     {
                        CreateProxyVaraibleDeclaration(gen, isAsync),
                        CreateProxyInvocationStatement(context, nameTable, sourceMethod)

                     }, new SyntaxNode[]
                     {
                        catchAndCloseProxyStatement
                     }
                  )
               });

            targetClass = gen.AddMembers(targetClass, targetMethod);

            if (isAsync && includeCancellableAsyncMethods)
            {
               targetMethod = gen.MethodDeclaration(sourceMethod);
               targetMethod = gen.AddParameters(targetMethod, new[] { gen.ParameterDeclaration(nameTable.CancellationTokenParameterName, gen.TypeExpression(RequireTypeSymbol<CancellationToken>(context))) });
               targetMethod = gen.WithModifiers(targetMethod, isAsync ? DeclarationModifiers.Async : DeclarationModifiers.None);


               targetMethod = gen.WithStatements(targetMethod, new SyntaxNode[]
                  {
                     // ==> try {
                     gen.TryCatchStatement(new SyntaxNode[]
                        {
                           CreateProxyVaraibleDeclaration(gen, isAsync),
                           CreateCancellableProxyInvocationStatement(context, nameTable, sourceMethod)

                        }, new SyntaxNode[]
                        {
                           catchAndCloseProxyStatement
                        }
                     )
                  });

               targetClass = gen.AddMembers(targetClass, targetMethod);
            }
         }

         #endregion

         #region Internal Methods

         targetClass = gen.AddMembers(targetClass, CreateGetProxyMethod(context, proxyInterface, nameTable, false));
         targetClass = gen.AddMembers(targetClass, CreateGetProxyMethod(context, proxyInterface, nameTable, true));
         targetClass = gen.AddMembers(targetClass, CreateStaticCloseProxyMethod(context, nameTable, false));
         targetClass = gen.AddMembers(targetClass, CreateStaticCloseProxyMethod(context, nameTable, true));
         targetClass = gen.AddMembers(targetClass, CreateCloseProxyMethod(context, nameTable, false));
         targetClass = gen.AddMembers(targetClass, CreateCloseProxyMethod(context, nameTable, true));
         targetClass = gen.AddMembers(targetClass, CreateEnsureProxyMethod(context, nameTable, false));
         targetClass = gen.AddMembers(targetClass, CreateEnsureProxyMethod(context, nameTable, true));
         targetClass = gen.AddMembers(targetClass, CreateDisposeMethods(context, nameTable));

         #endregion


         targetClass = AddGeneratedCodeAttribute(context.Generator, targetClass);
         return (ClassDeclarationSyntax)targetClass;
      }

      private SyntaxNode CreateProxyInvocationStatement(CSharpRoslynCodeGenerationContext context, ClientGenerationNameTable nameTable, IMethodSymbol sourceMethod)
      {
         bool isAsync = ReturnsTask(context, sourceMethod);
         bool isVoid = IsVoid(context, sourceMethod);

         SyntaxGenerator g = context.Generator;

         // proxy.Method(arg1, arg2, ...);
         SyntaxNode invocation = g.InvocationExpression(
                                    g.MemberAccessExpression(
                                       g.IdentifierName(nameTable.ProxyVariableName),
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

      private SyntaxNode CreateProxyVaraibleDeclaration(SyntaxGenerator g, bool isAsync)
      {         
         // var proxy = this.GetProxy();
         if (!isAsync)
         {
            return g.LocalDeclarationStatement("proxy",
                                             g.InvocationExpression(
                                                g.MemberAccessExpression(
                                                   g.ThisExpression(),
                                                   g.IdentifierName("GetProxy")
                                                )
                                             )
                                          );
         }
         else
         {
            // var proxy = await this.GetProxyAsync().ConfigureAwait(false);                                                
            return
               g.LocalDeclarationStatement("proxy",
                  g.AwaitExpression(
                     g.InvocationExpression(
                        g.MemberAccessExpression(
                           g.InvocationExpression(
                              g.MemberAccessExpression(
                                 g.ThisExpression(),
                                 g.IdentifierName("GetProxyAsync")
                              )
                           ), "ConfigureAwait"),
                        g.FalseLiteralExpression()
                     )
                  )
               );
         }
      }      

      private SyntaxNode CreateCancellableProxyInvocationStatement(CSharpRoslynCodeGenerationContext context, ClientGenerationNameTable nameTable, IMethodSymbol sourceMethod)
      {
         //using (cancellationToken.Register(s => CloseProxy((System.ServiceModel.ICommunicationObject)s, true), proxy, false))
         SyntaxGenerator g = context.Generator;
         string stateVariableName = GetUniqueParameterName("s", sourceMethod);         
         return g.UsingStatement(
            g.InvocationExpression(
               g.MemberAccessExpression(
                  g.IdentifierName(nameTable.CancellationTokenParameterName),
                     "Register"
               ),
               g.ValueReturningLambdaExpression(
                  stateVariableName,
                  g.InvocationExpression(
                     g.IdentifierName(nameTable.CloseProxyMethodName),
                     g.CastExpression(
                        RequireTypeSymbol(context, "System.ServiceModel.ICommunicationObject"),
                        g.IdentifierName(stateVariableName)
                     ),
                     g.TrueLiteralExpression()
                  )
               ),
               g.IdentifierName(nameTable.ProxyVariableName),
               g.FalseLiteralExpression()
            ),
            new SyntaxNode[]
            {
               CreateProxyInvocationStatement(context, nameTable, sourceMethod)
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

      private SyntaxNode CreateGetProxyMethod(CSharpRoslynCodeGenerationContext context, INamedTypeSymbol proxyInterface, ClientGenerationNameTable nameTable, bool isAsync)
      {
         SyntaxGenerator g = context.Generator;
         //private IProxy GetProxy()
         //{
         //   EnsureProxy();
         //   return m_cachedProxy;
         //}
         return g.MethodDeclaration(
            isAsync ? nameTable.GetProxyAsyncMethodName : nameTable.GetProxyMethodName,
            returnType: g.TypeExpression(isAsync ? GetGenericTaskType(context).Construct(proxyInterface) : proxyInterface),
            accessibility: Accessibility.Private,
            modifiers: isAsync ? DeclarationModifiers.Async : DeclarationModifiers.None,
            statements: new SyntaxNode[]
            {
               g.ExpressionStatement(
                  AwaitExpressionIfAsync(g, isAsync,
                     g.InvocationExpression(
                        g.MemberAccessExpression(
                           g.ThisExpression(),
                           isAsync ? nameTable.EnsureProxyAsyncMethodName : nameTable.EnsureProxyMethodName
                        )
                     )
                  )
               ),
               g.ReturnStatement(
                  g.MemberAccessExpression(
                     g.ThisExpression(),
                     nameTable.CachedProxyFieldName
                  )
               )
            }
         );
      }

      private SyntaxNode CreateStaticCloseProxyMethod(CSharpRoslynCodeGenerationContext context, ClientGenerationNameTable nameTable, bool asAsync)
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
         SyntaxGenerator g = context.Generator;
         return g.MethodDeclaration(
            asAsync ? nameTable.CloseProxyAsyncMethodName : nameTable.CloseProxyMethodName,
            accessibility: Accessibility.Private,
            returnType: asAsync ? g.TypeExpression(GetVoidTaskType(context)) : null,
            modifiers: (asAsync ? DeclarationModifiers.Async : DeclarationModifiers.None) | DeclarationModifiers.Static,
            parameters: new SyntaxNode[]
            {
               g.ParameterDeclaration("proxy", g.TypeExpression(RequireTypeSymbol(context, "System.ServiceModel.ICommunicationObject"))),
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
                     g.CatchClause(RequireTypeSymbol(context, "System.ServiceModel.CommunicationException"),
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
                     g.CatchClause(RequireTypeSymbol(context, "System.TimeoutException"),
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

      private SyntaxNode CreateCloseProxyMethod(CSharpRoslynCodeGenerationContext context, ClientGenerationNameTable nameTable, bool asAsync)
      {
         SyntaxGenerator g = context.Generator;
         return
            g.MethodDeclaration(
               asAsync ? nameTable.CloseProxyAsyncMethodName : nameTable.CloseProxyMethodName,
               returnType: asAsync ? g.TypeExpression(GetVoidTaskType(context)) : null,
               parameters: new SyntaxNode[] { g.ParameterDeclaration("alwaysAbort", g.TypeExpression(SpecialType.System_Boolean)) },
               modifiers: asAsync ? DeclarationModifiers.Async : DeclarationModifiers.None,
               accessibility: Accessibility.Private,
               statements: new SyntaxNode[]
               {
                  g.IfStatement(
                     g.ReferenceNotEqualsExpression(
                        g.IdentifierName(nameTable.CachedProxyFieldName),
                        g.NullLiteralExpression()
                     ),
                     new SyntaxNode[]
                     {
                        g.LocalDeclarationStatement(
                           "proxy",
                           g.TryCastExpression(
                              g.MemberAccessExpression(
                                 g.ThisExpression(),
                                 nameTable.CachedProxyFieldName
                              ),
                              RequireTypeSymbol(context, "System.ServiceModel.ICommunicationObject")
                           )
                        ),
                        g.TryFinallyStatement(
                           new SyntaxNode[]
                           {
                              AwaitExpressionIfAsync(g, asAsync,
                                 g.InvocationExpression(
                                    g.IdentifierName(asAsync ? nameTable.CloseProxyAsyncMethodName : nameTable.CloseProxyMethodName),
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
                                    nameTable.CachedProxyFieldName
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

      private SyntaxNode CreateEnsureProxyMethod(CSharpRoslynCodeGenerationContext context, ClientGenerationNameTable nameTable, bool asAsync)
      {
         SyntaxGenerator g = context.Generator;

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
               asAsync ? nameTable.EnsureProxyAsyncMethodName : nameTable.EnsureProxyMethodName,
               returnType: asAsync ? g.TypeExpression(GetVoidTaskType(context)) : null,
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
                              nameTable.CachedProxyFieldName
                           ),
                           g.NullLiteralExpression()
                        ),
                        g.LogicalOrExpression(
                           // ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Faulted
                           g.ValueEqualsExpression(
                              g.MemberAccessExpression(
                                 g.CastExpression(
                                    RequireTypeSymbol(context, "System.ServiceModel.ICommunicationObject"),
                                    g.MemberAccessExpression(
                                       g.ThisExpression(),
                                       nameTable.CachedProxyFieldName
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
                                    RequireTypeSymbol(context, "System.ServiceModel.ICommunicationObject"),
                                    g.MemberAccessExpression(
                                       g.ThisExpression(),
                                       nameTable.CachedProxyFieldName
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
                                 asAsync ? nameTable.CloseProxyAsyncMethodName : nameTable.CloseProxyMethodName
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
                           nameTable.CachedProxyFieldName
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
                                 nameTable.ProxyFactoryFieldName
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
                                              RequireTypeSymbol(context, "System.ServiceModel.ICommunicationObject"),
                                              g.IdentifierName("proxy")
                                          ),
                                          "BeginOpen"
                                       ),
                                       g.MemberAccessExpression(
                                          g.CastExpression(
                                              RequireTypeSymbol(context, "System.ServiceModel.ICommunicationObject"),
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
                                    RequireTypeSymbol(context, "System.ServiceModel.ICommunicationObject"),
                                    g.IdentifierName("proxy")
                                 ),
                                 "Open"
                              )
                           )
                        ),
                        g.AssignmentStatement(
                           g.MemberAccessExpression(
                              g.ThisExpression(),
                              nameTable.CachedProxyFieldName
                           ),
                           g.IdentifierName("proxy")
                        )                        
                     }
                  )
               }
            );

      }

      private IEnumerable<SyntaxNode> CreateDisposeMethods(CSharpRoslynCodeGenerationContext context, ClientGenerationNameTable nameTable)
      {
         SyntaxGenerator g = context.Generator;

         yield return g.MethodDeclaration(
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
                     g.TypeExpression(RequireTypeSymbol(context, "System.GC")),
                     "SuppressFinalize"
                  ),
                  g.ThisExpression()
               )
            }
         ).PrependLeadingTrivia(g.CreateRegionTrivia("IDisposable"));


         yield return g.MethodDeclaration(
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
                                    nameTable.CloseProxyMethodName
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
         ).AddTrailingTrivia(g.CreateEndRegionTrivia());
      }
   }
}

#if false


#region Disposable

      public void Dispose()
      {
         Dispose(true);
         System.GC.SuppressFinalize(this);
      }

      private void Dispose(bool disposing)
      {
         if (disposing)
         {
            try
            {
               CloseProxy(false);
            }
            catch
            {
            }
         }
      }
#endregion
   }
}
#endif


