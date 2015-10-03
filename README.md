# WCF Client Proxy Generator (C#)

The WCF Client Proxy Generator is a Visual Studio Extension providing a Custom Tool that can be used for generating WCF Client proxy interfaces and classes based on service contract interfaces defined in code in the same solution.

## Introduction
When developing an application where both the WCF service and client are in the same solution, the "Add Service Reference" command in Visual Studio is not ideal to use since it requires a running service.  Implementing the client manually is tedious to say the least, especially keeping it updated when the service contract changes. 

In addition, when defining the service you usually have either an async or sync version of each operation contract. In the client however, you may want both.  This may mean that you need a different interface for the client proxy as well.

Then there is the problem of error handling when using client proxies deriving from [`ClientBase`](https://msdn.microsoft.com/en-us/library/ms576141%28v=vs.110%29.aspx) or [`DuplexClientBase`](https://msdn.microsoft.com/en-us/library/ms576169%28v=vs.110%29.aspx). You should avoid using the `using` statement, because the `Dispose` method may actually throw exceptions, which would hide any original exception that was generated.

The WCF Client Proxy Generator custom tool attempts to solve the above problems.

## Usage

This extension generates partial classes and interfaces based on partial class/interface definitions that are decorated with an attribute containng options controlling the generation. There are three attributes recognized by this extension; `GenerateWcfClientProxyAttribute`, `GenerateErrorHandlingWcfProxyAttribute` and `GenerateErrorHandlingWcfProxyWrapperAttribute`. The extension ignores the namespace in which these attributes are defined, only their name matters.  The source code for these attributes can be found in the NuGet package [Alphaleonis.WcfClientProxyGenerator.Attributes](https://www.nuget.org/packages/Alphaleonis.WcfClientProxyGenerator.Attributes/). The source code is also available at the end of this document.
 
After this is done, you create a normal C# class file (`.cs`) and set the *Custom Tool* property of the file to `WcfClientProxyGenerator`. Then you add partial class- and/or interface declarations that are tagged with the attributes described above depending on what you want to generate. The code is then generated any time you save that file, with a `.g.cs` extension.

### Generating a Client Proxy Interface

To generate a client proxy interface based on a service interface you define an empty partial interface in your `.cs` file and decorate it with the `GenerateWcfClientProxyAttribute`.

For example:

	[GenerateWcfClientProxy(typeof(IMyService))]
	public partial interface IMyProxy 
	{
	}    

This will generate an interface that contains all methods decorated with `[OperationContract]` in the `IMyService` interface, in both TPL async based, and normal synchronous versions. The `[ServiceContract]` attribute from `IMyService` is also copied to the generated interface.

If you want only synchronous methods generated, you can specify the `SuppressAsyncMethods` parameter to the attribute.

You can also use a fully qualified type name instead of a Type if your client assembly has no direct reference to the assembly containing the service interface, for example:

	[GenerateWcfClientProxy("ClassLibrary1.IMyService", SuppressAsyncMethods = false)]
	public partial interface IMyProxy : IDisposable
	{
	}

If you want your client interface to implement the service interface, or any other interfaces, this can be done as well. If the template interface implements the service interface, only the sync/async versions of the operation contracts not defined in the service interface are generated in the client interface. 
 

### Generating a Simple Client Proxy Class

This is done much the same way as the client proxy interface above, with the difference that you define a class instead of an interface. In this case however, only the methods actually defined in the interface are defined on the generated class.  A common scenario in this case therefore is to generate a proxy interface as described in the previous section to get both async and sync versions of all operations, and then generate the proxy class from that.  This can be done in the same file, for example:

	[GenerateWcfClientProxy("ClassLibrary1.IMyService")]
	public partial interface IMyProxy
	{
	}
	
	[GenerateWcfClientProxy(typeof(IMyProxy))]
	public partial class MyProxy 
	{
	}

This will generate a class derived from `ClientBase` (or `DuplexClientBase` if a callback contract is defined on the service interface), also implementing the interface specified, much the same way that "Add service reference" in Visual Studio works.

### Generating an Error Handling Proxy Wrapper Class

The error handling proxy wrapper, is a wrapper class that is responsible for creating and maintaining the lifetime of the actual proxy class, such as the one generated in the previous section.  This class can be used in a `using` statement, since the error handling is taken care of internally.

This class implements the service interface, much the same way as the proxy class, but for each operation called, a new instance of the proxy is created if one does not already exist that is not in the `Faulted` state. If the proxy faults, a new instance will be created for the next operation called. The implementation is based on ideas from several sources, for example the blog entry ["A smarter WCF service client"](http://blogs.msmvps.com/p3net/2014/02/02/a-smarter-wcf-service-client-part-1/). 
 
To generate such a wrapper you would use the attribute `GenerateErrorHandlingWcfProxyWrapper` on your template class instead. For example:

	[GenerateErrorHandlingWcfProxyWrapper(typeof(IMyProxy))]
	public partial class MyProxy 
	{
	}

This will generate a class with a single constructor accepting a factory method for creating instances of the actual proxy class:

	public MyProxy(Func<IMyProxy> proxyFactory)
	{
	   // ...
	}

The `proxyFactory` specified must return an instance that implements both `IMyProxy` in this case *and* `ICommunicationObject`. This is the case for any channel created by a `ChannelFactory` or class derived from `ClientBase` etc.

There are a few options that can be specified for this attribute, for example:

	[GenerateErrorHandlingWcfProxyWrapper(typeof(IMyProxy), ConstructorVisibility = GeneratedMemberAccessibility.Protected)]
	public partial class MyProxy 
	{
	}

The `ConstructorVisibility` parameter determines how the declared accessibility of the generated constructor. You may want this `private` for example, if your partial class contains a public constructor that supplies the factory from somewhere else, for example:

	[GenerateErrorHandlingWcfProxyWrapper(typeof(IMyProxy), ConstructorVisibility = GeneratedMemberAccessibility.Private)]
	public partial class MyProxy 
	{
	    public MyProxy(string endpointConfigurationName)
	        : this(() => new MyClientBaseProxy(endpointConfigurationName))
	    {
	    }
	}

### Generating an Error Handling Proxy

This is essentially a combination of generating both an error handling wrapper and an internal proxy class. The generated class will have the same list of constructors as `ClientBase` (or `DuplexClientBase`), but it is an error handling wrapper with the same features of the wrapper described above.

To generate such a class  you follow the same procedure as above, but you use the attribute `GenerateErrorHandlingWcfProxy]` instead. 
 
For example:

	[GenerateErrorHandlingWcfProxy(typeof(IMyProxy), ConstructorVisibility = GeneratedMemberAccessibility.Public)]
	public partial class MyProxy 
	{
	}

This will generate an error handling wrapper, with the same constructors as the proxy would have. Internally it will also generate a nested class that is the actual proxy derived from `ClientBase` or `DuplexClientBase` with the constructors providing factories for the error handling wrapper.

## Appendix

### Attributes

Below follows the source code for the attributes that can be used to control code generation by this extension.
	
	[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
	public class GenerateWcfClientProxyAttribute : Attribute
	{
	  private readonly string m_sourceInterfaceTypeName;
	  private readonly Type m_sourceInterfaceType;
	
	  public GenerateWcfClientProxyAttribute(string sourceInterfaceTypeName)
	  {
	     m_sourceInterfaceTypeName = sourceInterfaceTypeName;
	  }
	
	  public GenerateWcfClientProxyAttribute(Type sourceInterfaceType)
	     : this(sourceInterfaceType.FullName)
	  {
	     m_sourceInterfaceType = sourceInterfaceType;
	  }
	
	  public Type SourceInterfaceType
	  {
	     get
	     {
	        return m_sourceInterfaceType;
	     }
	  }
	
	  public string SourceInterfaceTypeName
	  {
	     get
	     {
	        return m_sourceInterfaceTypeName;
	     }
	  }
	
	  public bool SuppressAsyncMethods { get; set; }
	
	  public bool SuppressWarningComments { get; set; }
	}
	
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
	public class GenerateErrorHandlingWcfProxyAttribute : GenerateWcfClientProxyAttribute
	{
	  private GeneratedMemberAccessibility m_constructorVisibility = GeneratedMemberAccessibility.Public;
	
	  public GenerateErrorHandlingWcfProxyAttribute(string sourceInterfaceTypeName)
	     : base(sourceInterfaceTypeName)
	  {
	  }
	
	  public GenerateErrorHandlingWcfProxyAttribute(Type sourceInterfaceType)
	     : base(sourceInterfaceType)
	  {
	  }
	
	  public GeneratedMemberAccessibility ConstructorVisibility
	  {
	     get
	     {
	        return m_constructorVisibility;
	     }
	
	     set
	     {
	        m_constructorVisibility = value;
	     }
	  }
	}
	
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
	public class GenerateErrorHandlingWcfProxyWrapperAttribute : GenerateErrorHandlingWcfProxyAttribute
	{
	  public GenerateErrorHandlingWcfProxyWrapperAttribute(string sourceInterfaceTypeName)
	     : base(sourceInterfaceTypeName)
	  {
	  }
	
	  public GenerateErrorHandlingWcfProxyWrapperAttribute(Type sourceInterfaceType)
	     : base(sourceInterfaceType)
	  {
	  }      
	}
	
	public enum GeneratedMemberAccessibility
	{
	  Public,
	  Protected,
	  Internal,
	  Private,
	  ProtectedInternal
	}