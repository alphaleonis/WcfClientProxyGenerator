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

      public ClassDeclarationSyntax GenerateClientClass(INamedTypeSymbol proxyInterface, string name = null, Accessibility accessibility = Accessibility.Public, bool includeCancellableAsyncMethods = true)
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

         SyntaxGenerator gen = Context.Generator;
         SyntaxNode targetClass = gen.ClassDeclaration(name, baseType: gen.TypeExpression(RequireTypeSymbol<MarshalByRefObject>()), accessibility: accessibility, modifiers: DeclarationModifiers.Sealed);
         targetClass = gen.AddInterfaceType(targetClass, gen.TypeExpression(Context.Compilation.GetSpecialType(SpecialType.System_IDisposable)));
         targetClass = gen.AddInterfaceType(targetClass, gen.TypeExpression(proxyInterface));

         IEnumerable<IMethodSymbol> methods = GetOperationContractMethods(proxyInterface).ToArray();

         ClientGenerationNameTable nameTable = new ClientGenerationNameTable(methods.Select(m => m.Name));


         #region Private Fields

         // ==> private IProxy m_cachedProxy;
         targetClass = gen.AddMembers(targetClass, gen.FieldDeclaration(nameTable.CachedProxyFieldName, gen.TypeExpression(proxyInterface), Accessibility.Private, DeclarationModifiers.None));

         // ==> private readonly Func<IProxy> m_proxyFactory;
         SyntaxNode proxyFactoryTypeExpression = gen.TypeExpression(Context.Compilation.GetTypeByMetadataName("System.Func`1").Construct(proxyInterface));
         targetClass = gen.AddMembers(targetClass, gen.FieldDeclaration(nameTable.ProxyFactoryFieldName, proxyFactoryTypeExpression, Accessibility.Private, DeclarationModifiers.ReadOnly));

         #endregion

         #region Constructor

         // Constructor
         SyntaxGenerator generator = gen;
         SyntaxNode constructor = generator.ConstructorDeclaration(parameters: new[] { gen.ParameterDeclaration("proxyFactory", proxyFactoryTypeExpression) }, accessibility: Accessibility.Public);

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

            bool isAsync = ReturnsTask(sourceMethod);
            bool isVoid = sourceMethod.ReturnType.SpecialType == SpecialType.System_Void || sourceMethod.ReturnType.Equals(VoidTaskType);

            SyntaxNode targetMethod = gen.MethodDeclaration(sourceMethod);
            targetMethod = gen.WithModifiers(targetMethod, isAsync ? DeclarationModifiers.Async : DeclarationModifiers.None);


            targetMethod = gen.WithStatements(targetMethod, new SyntaxNode[]
               {
                  // ==> try {
                  gen.TryCatchStatement(new SyntaxNode[]
                     {
                        CreateProxyVaraibleDeclaration(isAsync),
                        CreateProxyInvocationStatement(nameTable, sourceMethod)

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
               targetMethod = gen.AddParameters(targetMethod, new[] { gen.ParameterDeclaration(nameTable.CancellationTokenParameterName, gen.TypeExpression(RequireTypeSymbol<CancellationToken>())) });
               targetMethod = gen.WithModifiers(targetMethod, isAsync ? DeclarationModifiers.Async : DeclarationModifiers.None);


               targetMethod = gen.WithStatements(targetMethod, new SyntaxNode[]
                  {
                     // ==> try {
                     gen.TryCatchStatement(new SyntaxNode[]
                        {
                           CreateProxyVaraibleDeclaration(isAsync),
                           CreateCancellableProxyInvocationStatement(nameTable, sourceMethod)

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

         targetClass = gen.AddMembers(targetClass, CreateGetProxyMethod(proxyInterface, nameTable, false));
         targetClass = gen.AddMembers(targetClass, CreateGetProxyMethod(proxyInterface, nameTable, true));
         targetClass = gen.AddMembers(targetClass, CreateStaticCloseProxyMethod(nameTable, false));
         targetClass = gen.AddMembers(targetClass, CreateStaticCloseProxyMethod(nameTable, true));
         targetClass = gen.AddMembers(targetClass, CreateCloseProxyMethod(nameTable, false));
         targetClass = gen.AddMembers(targetClass, CreateCloseProxyMethod(nameTable, true));
         targetClass = gen.AddMembers(targetClass, CreateEnsureProxyMethod(nameTable, false));
         targetClass = gen.AddMembers(targetClass, CreateEnsureProxyMethod(nameTable, true));
         targetClass = gen.AddMembers(targetClass, CreateDisposeMethods(nameTable));

         #endregion


         targetClass = AddGeneratedCodeAttribute(targetClass);
         return (ClassDeclarationSyntax)targetClass;
      }

      private SyntaxNode CreateProxyInvocationStatement(ClientGenerationNameTable nameTable, IMethodSymbol sourceMethod)
      {
         bool isAsync = ReturnsTask(sourceMethod);
         bool isVoid = IsVoid(sourceMethod);

         // proxy.Method(arg1, arg2, ...);
         SyntaxNode invocation = Context.Generator.InvocationExpression(
                                    Context.Generator.MemberAccessExpression(
                                       Context.Generator.IdentifierName(nameTable.ProxyVariableName),
                                       sourceMethod.Name
                                    ),
                                    sourceMethod.Parameters.Select(p => Context.Generator.IdentifierName(p.Name))
                                 );

         if (isAsync)
         {
            // await proxy.Method(arg1, arg2, ...).ConfigureAwait(false);
            invocation =
               Context.Generator.AwaitExpression(
                  Context.Generator.InvocationExpression(
                     Context.Generator.MemberAccessExpression(
                        invocation,
                        "ConfigureAwait"
                     ),
                     Context.Generator.FalseLiteralExpression()
                  )
               );
         }

         if (isVoid)
         {
            invocation = Context.Generator.ExpressionStatement(invocation);
         }
         else
         {
            invocation = Context.Generator.ReturnStatement(invocation);
         }

         return invocation;

      }

      private SyntaxNode CreateProxyVaraibleDeclaration(bool isAsync)
      {
         // var proxy = this.GetProxy();
         if (!isAsync)
         {
            return Context.Generator.LocalDeclarationStatement("proxy",
                                             Context.Generator.InvocationExpression(
                                                Context.Generator.MemberAccessExpression(
                                                   Context.Generator.ThisExpression(),
                                                   Context.Generator.IdentifierName("GetProxy")
                                                )
                                             )
                                          );
         }
         else
         {
            // var proxy = await this.GetProxyAsync().ConfigureAwait(false);                                                
            return
               Context.Generator.LocalDeclarationStatement("proxy",
                  Context.Generator.AwaitExpression(
                     Context.Generator.InvocationExpression(
                        Context.Generator.MemberAccessExpression(
                           Context.Generator.InvocationExpression(
                              Context.Generator.MemberAccessExpression(
                                 Context.Generator.ThisExpression(),
                                 Context.Generator.IdentifierName("GetProxyAsync")
                              )
                           ), "ConfigureAwait"),
                        Context.Generator.FalseLiteralExpression()
                     )
                  )
               );
         }
      }      

      private SyntaxNode CreateCancellableProxyInvocationStatement(ClientGenerationNameTable nameTable, IMethodSymbol sourceMethod)
      {
         //using (cancellationToken.Register(s => CloseProxy((System.ServiceModel.ICommunicationObject)s, true), proxy, false))
         string stateVariableName = GetUniqueParameterName("s", sourceMethod);
         return Context.Generator.UsingStatement(
            Context.Generator.InvocationExpression(
               Context.Generator.MemberAccessExpression(
                  Context.Generator.IdentifierName(nameTable.CancellationTokenParameterName),
                  "Register"
               ),
               Context.Generator.ValueReturningLambdaExpression(
                  stateVariableName,
                  Context.Generator.InvocationExpression(
                     Context.Generator.IdentifierName(nameTable.CloseProxyMethodName),
                     Context.Generator.CastExpression(
                        RequireTypeSymbol("System.ServiceModel.ICommunicationObject"),
                        Context.Generator.IdentifierName(stateVariableName)
                     ),
                     Context.Generator.TrueLiteralExpression()
                  )
               ),
               Context.Generator.IdentifierName(nameTable.ProxyVariableName),
               Context.Generator.FalseLiteralExpression()
            ),
            new SyntaxNode[]
            {
               CreateProxyInvocationStatement(nameTable, sourceMethod)
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

      private SyntaxNode CreateGetProxyMethod(INamedTypeSymbol proxyInterface, ClientGenerationNameTable nameTable, bool isAsync)
      {
         //private IProxy GetProxy()
         //{
         //   EnsureProxy();
         //   return m_cachedProxy;
         //}
         return Context.Generator.MethodDeclaration(
            isAsync ? nameTable.GetProxyAsyncMethodName : nameTable.GetProxyMethodName,
            returnType: Context.Generator.TypeExpression(isAsync ? GenericTaskType.Construct(proxyInterface) : proxyInterface),
            accessibility: Accessibility.Private,
            modifiers: isAsync ? DeclarationModifiers.Async : DeclarationModifiers.None,
            statements: new SyntaxNode[]
            {
               Context.Generator.ExpressionStatement(
                  AwaitExpressionIfAsync(isAsync,
                     Context.Generator.InvocationExpression(
                        Context.Generator.MemberAccessExpression(
                           Context.Generator.ThisExpression(),
                           isAsync ? nameTable.EnsureProxyAsyncMethodName : nameTable.EnsureProxyMethodName
                        )
                     )
                  )
               ),
               Context.Generator.ReturnStatement(
                  Context.Generator.MemberAccessExpression(
                     Context.Generator.ThisExpression(),
                     nameTable.CachedProxyFieldName
                  )
               )
            }
         );
      }

      private SyntaxNode CreateStaticCloseProxyMethod(ClientGenerationNameTable nameTable, bool asAsync)
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
         return Context.Generator.MethodDeclaration(
            asAsync ? nameTable.CloseProxyAsyncMethodName : nameTable.CloseProxyMethodName,
            accessibility: Accessibility.Private,
            returnType: asAsync ? Context.Generator.TypeExpression(VoidTaskType) : null,
            modifiers: (asAsync ? DeclarationModifiers.Async : DeclarationModifiers.None) | DeclarationModifiers.Static,
            parameters: new SyntaxNode[]
            {
               Context.Generator.ParameterDeclaration("proxy", Context.Generator.TypeExpression(RequireTypeSymbol("System.ServiceModel.ICommunicationObject"))),
               Context.Generator.ParameterDeclaration("alwaysAbort", Context.Generator.TypeExpression(SpecialType.System_Boolean))

            },
            statements: new SyntaxNode[]
            {
               Context.Generator.TryCatchStatement(
                  new SyntaxNode[]
                  {
                     // if (proxy != null && proxy.State != System.ServiceModel.CommunicationState.Closed)
                     Context.Generator.IfStatement(
                        Context.Generator.LogicalAndExpression(
                           Context.Generator.ReferenceNotEqualsExpression(
                              Context.Generator.IdentifierName("proxy"),
                              Context.Generator.NullLiteralExpression()
                           ),
                           Context.Generator.ValueNotEqualsExpression(
                              Context.Generator.MemberAccessExpression(
                                 Context.Generator.IdentifierName("proxy"),
                                 "State"
                              ),
                              Context.Generator.DottedName("System.ServiceModel.CommunicationState.Closed")
                           )
                        ),
                        new SyntaxNode[]
                        {
                           // if (!alwaysAbort && proxy.State != System.ServiceModel.CommunicationState.Faulted)
                           Context.Generator.IfStatement(
                              Context.Generator.LogicalAndExpression(
                                 Context.Generator.LogicalNotExpression(
                                    Context.Generator.IdentifierName("alwaysAbort")
                                 ),
                                 Context.Generator.ValueNotEqualsExpression(
                                    Context.Generator.MemberAccessExpression(
                                       Context.Generator.IdentifierName("proxy"),
                                       "State"
                                    ),
                                    Context.Generator.DottedName("System.ServiceModel.CommunicationState.Faulted")
                                 )
                              ),
                              new SyntaxNode[]
                              {
                                 Context.Generator.ExpressionStatement(
                                 asAsync ? 
                                 // await System.Threading.Tasks.Task.Factory.FromAsync(proxy.BeginClose, proxy.EndClose, null).ConfigureAwait(false);
                                 AwaitExpression(
                                    Context.Generator.InvocationExpression(                                            
                                       Context.Generator.DottedName("System.Threading.Tasks.Task.Factory.FromAsync"),
                                       Context.Generator.DottedName("proxy.BeginClose"),
                                       Context.Generator.DottedName("proxy.EndClose"),
                                       Context.Generator.NullLiteralExpression()                                                                              
                                    )
                                 ) 
                                 :
                                 // proxy.Close();
                                 Context.Generator.InvocationExpression(
                                       Context.Generator.MemberAccessExpression(
                                          Context.Generator.IdentifierName("proxy"),
                                          "Close"
                                       )
                                    )
                                 )
                              },
                              new SyntaxNode[]
                              {
                                 // proxy.Abort();
                                 Context.Generator.ExpressionStatement(
                                    Context.Generator.InvocationExpression(
                                       Context.Generator.MemberAccessExpression(
                                          Context.Generator.IdentifierName("proxy"),
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
                     Context.Generator.CatchClause(RequireTypeSymbol("System.ServiceModel.CommunicationException"),
                        new SyntaxNode[]
                        {
                           // proxy.Abort();
                           Context.Generator.ExpressionStatement(
                              Context.Generator.InvocationExpression(
                                 Context.Generator.MemberAccessExpression(
                                    Context.Generator.IdentifierName("proxy"),
                                    "Abort"
                                 )
                              )
                           )
                        }
                     ),
                     Context.Generator.CatchClause(RequireTypeSymbol("System.TimeoutException"),
                        new SyntaxNode[]
                        {
                           // proxy.Abort();
                           Context.Generator.ExpressionStatement(
                              Context.Generator.InvocationExpression(
                                 Context.Generator.MemberAccessExpression(
                                    Context.Generator.IdentifierName("proxy"),
                                    "Abort"
                                 )
                              )
                           )
                        }
                     ),
                     Context.Generator.CatchClause(
                        new SyntaxNode[]
                        {
                           // proxy.Abort();
                           Context.Generator.ExpressionStatement(
                              Context.Generator.InvocationExpression(
                                 Context.Generator.MemberAccessExpression(
                                    Context.Generator.IdentifierName("proxy"),
                                    "Abort"
                                 )
                              )
                           ),
                           Context.Generator.ThrowStatement()
                        }
                     )

                  }
               )
            }
         );
      }

      private SyntaxNode CreateCloseProxyMethod(ClientGenerationNameTable nameTable, bool asAsync)
      {         
         return
            Context.Generator.MethodDeclaration(
               asAsync ? nameTable.CloseProxyAsyncMethodName : nameTable.CloseProxyMethodName,
               returnType: asAsync ? Context.Generator.TypeExpression(VoidTaskType) : null,
               parameters: new SyntaxNode[] { Context.Generator.ParameterDeclaration("alwaysAbort", Context.Generator.TypeExpression(SpecialType.System_Boolean)) },
               modifiers: asAsync ? DeclarationModifiers.Async : DeclarationModifiers.None,
               accessibility: Accessibility.Private,
               statements: new SyntaxNode[]
               {
                  Context.Generator.IfStatement(
                     Context.Generator.ReferenceNotEqualsExpression(
                        Context.Generator.IdentifierName(nameTable.CachedProxyFieldName),
                        Context.Generator.NullLiteralExpression()
                     ),
                     new SyntaxNode[]
                     {
                        Context.Generator.LocalDeclarationStatement(
                           "proxy",
                           Context.Generator.TryCastExpression(                              
                              Context.Generator.MemberAccessExpression(
                                 Context.Generator.ThisExpression(),
                                 nameTable.CachedProxyFieldName
                              ),
                              RequireTypeSymbol("System.ServiceModel.ICommunicationObject")
                           )
                        ),
                        Context.Generator.TryFinallyStatement(
                           new SyntaxNode[]
                           {
                              AwaitExpressionIfAsync(asAsync, 
                                 Context.Generator.InvocationExpression(
                                    Context.Generator.IdentifierName(asAsync ? nameTable.CloseProxyAsyncMethodName : nameTable.CloseProxyMethodName),
                                    Context.Generator.IdentifierName("proxy"),
                                    Context.Generator.IdentifierName("alwaysAbort")
                                 )
                              )
                           },
                           new SyntaxNode[]
                           {
                              Context.Generator.AssignmentStatement(
                                 Context.Generator.MemberAccessExpression(
                                    Context.Generator.ThisExpression(),
                                    nameTable.CachedProxyFieldName
                                 ),
                                 Context.Generator.NullLiteralExpression()
                              )
                           }
                        )
                     }
                  )
               }
            );
      }

      private SyntaxNode CreateEnsureProxyMethod(ClientGenerationNameTable nameTable, bool asAsync)
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
            Context.Generator.MethodDeclaration(
               asAsync ? nameTable.EnsureProxyAsyncMethodName : nameTable.EnsureProxyMethodName,
               returnType: asAsync ? Context.Generator.TypeExpression(VoidTaskType) : null,
               accessibility: Accessibility.Private,
               modifiers: asAsync ? DeclarationModifiers.Async : DeclarationModifiers.None,
               statements: new SyntaxNode[]
               {
                  //if (m_cachedProxy != null && (
                  //             ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Faulted ||
                  //             ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Closed))
                  Context.Generator.IfStatement(
                     Context.Generator.LogicalAndExpression(
                        // m_cachedProxy != null
                        Context.Generator.ReferenceNotEqualsExpression(
                           Context.Generator.MemberAccessExpression(
                              Context.Generator.ThisExpression(),
                              nameTable.CachedProxyFieldName
                           ),
                           Context.Generator.NullLiteralExpression()
                        ),
                        Context.Generator.LogicalOrExpression(
                           // ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Faulted
                           Context.Generator.ValueEqualsExpression(
                              Context.Generator.MemberAccessExpression(
                                 Context.Generator.CastExpression(
                                    RequireTypeSymbol("System.ServiceModel.ICommunicationObject"),
                                    Context.Generator.MemberAccessExpression(
                                       Context.Generator.ThisExpression(),
                                       nameTable.CachedProxyFieldName
                                    )
                                 ),
                                 "State"
                              ),
                              Context.Generator.DottedName("System.ServiceModel.CommunicationState.Faulted")
                           ),
                           // ((System.ServiceModel.ICommunicationObject)m_cachedProxy).State == System.ServiceModel.CommunicationState.Faulted
                           Context.Generator.ValueEqualsExpression(
                              Context.Generator.MemberAccessExpression(
                                 Context.Generator.CastExpression(
                                    RequireTypeSymbol("System.ServiceModel.ICommunicationObject"),
                                    Context.Generator.MemberAccessExpression(
                                       Context.Generator.ThisExpression(),
                                       nameTable.CachedProxyFieldName
                                    )
                                 ),
                                 "State"
                              ),
                              Context.Generator.DottedName("System.ServiceModel.CommunicationState.Closed")
                           )
                        )
                     ),
                     new SyntaxNode[]
                     {
                        // await CloseProxyAsync(false).ConfigureAwait(false);
                        // or
                        // CloseProxy(false);
                        AwaitExpressionIfAsync(asAsync,
                           Context.Generator.InvocationExpression(
                              Context.Generator.MemberAccessExpression(
                                 Context.Generator.ThisExpression(),
                                 asAsync ? nameTable.CloseProxyAsyncMethodName : nameTable.CloseProxyMethodName
                              ),
                              Context.Generator.FalseLiteralExpression()
                           )
                        )
                     }
                  ),
                  Context.Generator.IfStatement(
                     Context.Generator.ReferenceEqualsExpression(
                        Context.Generator.MemberAccessExpression(
                           Context.Generator.ThisExpression(),
                           nameTable.CachedProxyFieldName
                        ),
                        Context.Generator.NullLiteralExpression()
                     ),
                     new SyntaxNode[]
                     {
                        Context.Generator.LocalDeclarationStatement(
                           "proxy",
                           Context.Generator.InvocationExpression(
                              Context.Generator.MemberAccessExpression(
                                 Context.Generator.ThisExpression(),
                                 nameTable.ProxyFactoryFieldName
                              )
                           )
                        ),
                        Context.Generator.ExpressionStatement(
                           asAsync ?
                           AwaitExpression(
                                    Context.Generator.InvocationExpression(
                                       Context.Generator.DottedName("System.Threading.Tasks.Task.Factory.FromAsync"),
                                       Context.Generator.MemberAccessExpression(
                                          Context.Generator.CastExpression(
                                              RequireTypeSymbol("System.ServiceModel.ICommunicationObject"),
                                              Context.Generator.IdentifierName("proxy")
                                          ),
                                          "BeginOpen"
                                       ),
                                       Context.Generator.MemberAccessExpression(
                                          Context.Generator.CastExpression(
                                              RequireTypeSymbol("System.ServiceModel.ICommunicationObject"),
                                              Context.Generator.IdentifierName("proxy")
                                          ),
                                          "EndOpen"
                                       ),
                                       Context.Generator.NullLiteralExpression()
                                    )
                                 )
                           :
                           Context.Generator.InvocationExpression(
                              Context.Generator.MemberAccessExpression(
                                 Context.Generator.CastExpression(
                                    RequireTypeSymbol("System.ServiceModel.ICommunicationObject"),
                                    Context.Generator.IdentifierName("proxy")
                                 ),
                                 "Open"
                              )
                           )
                        ),
                        Context.Generator.AssignmentStatement(
                           Context.Generator.MemberAccessExpression(
                              Context.Generator.ThisExpression(),
                              nameTable.CachedProxyFieldName
                           ),
                           Context.Generator.IdentifierName("proxy")
                        )                        
                     }
                  )
               }
            );

      }

      private IEnumerable<SyntaxNode> CreateDisposeMethods(ClientGenerationNameTable nameTable)
      {
         yield return Context.Generator.MethodDeclaration(
            "Dispose",
            accessibility: Accessibility.Public,
            statements: new SyntaxNode[]
            {
               Context.Generator.InvocationExpression(
                  Context.Generator.MemberAccessExpression(
                     Context.Generator.ThisExpression(),
                     "Dispose"
                  ),
                  Context.Generator.TrueLiteralExpression()
               ),
               Context.Generator.InvocationExpression(
                  Context.Generator.MemberAccessExpression(
                     Context.Generator.TypeExpression(RequireTypeSymbol("System.GC")),
                     "SuppressFinalize"
                  ),
                  Context.Generator.ThisExpression()
               )
            }
         ).PrependLeadingTrivia(Context.Generator.CreateRegionTrivia("IDisposable"));


         yield return Context.Generator.MethodDeclaration(
            "Dispose",
            parameters: new SyntaxNode[] { Context.Generator.ParameterDeclaration("disposing", Context.Generator.TypeExpression(SpecialType.System_Boolean)) },
            accessibility: Accessibility.Private,
            statements: new SyntaxNode[]
            {
               Context.Generator.IfStatement(
                  Context.Generator.ValueEqualsExpression(
                     Context.Generator.IdentifierName("disposing"),
                     Context.Generator.TrueLiteralExpression()
                  ),
                  new SyntaxNode[]
                  {
                     Context.Generator.TryCatchStatement(
                        new SyntaxNode[]
                        {
                           Context.Generator.ExpressionStatement(
                              Context.Generator.InvocationExpression(
                                 Context.Generator.MemberAccessExpression(
                                    Context.Generator.ThisExpression(),
                                    nameTable.CloseProxyMethodName
                                 ),
                                 Context.Generator.FalseLiteralExpression()
                              )
                           )
                        },
                        new SyntaxNode[]
                        {
                           Context.Generator.CatchClause(new SyntaxNode [0])
                        }
                     )
                  }
               )
            }
         ).AddTrailingTrivia(Context.Generator.CreateEndRegionTrivia());
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


