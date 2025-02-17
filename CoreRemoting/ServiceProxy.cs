using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Castle.DynamicProxy;
using CoreRemoting.RemoteDelegates;
using CoreRemoting.Serialization.Bson;
using Serialize.Linq.Extensions;
using stakx.DynamicProxy;

namespace CoreRemoting
{
    /// <summary>
    /// Implements a proxy of a remote service that is hosted on a CoreRemoting server..
    /// This is doing the RPC magic of CoreRemoting at client side.
    /// </summary>
    /// <typeparam name="TServiceInterface">Type of the remote service's interface (also known as contract of the service)</typeparam>
    public class ServiceProxy<TServiceInterface> : AsyncInterceptor, IServiceProxy
    {
        private readonly string _serviceName;
        private RemotingClient _client;

        /// <summary>
        /// Creates a new instance of the ServiceProxy class.
        /// </summary>
        /// <param name="client">CoreRemoting client to be used for client/server communication</param>
        /// <param name="serviceName">Unique name of the remote service</param>
        public ServiceProxy(RemotingClient client, string serviceName = "")
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));

            var serviceInterfaceType = typeof(TServiceInterface);

            _serviceName =
                string.IsNullOrWhiteSpace(serviceName)
                    ? serviceInterfaceType.FullName
                    : serviceName;
        }

        /// <summary>
        /// Finalizer.
        /// </summary>
        ~ServiceProxy()
        {
            ((IServiceProxy)this).Shutdown();
        }

        /// <summary>
        /// Shutdown service proxy and free resources.
        /// </summary>
        void IServiceProxy.Shutdown()
        {
            if (_client != null)
            {
                _client.ClientDelegateRegistry.UnregisterClientDelegatesOfServiceProxy(this);
                _client = null;
            }

            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Intercepts a synchronous call of a member on the proxy object. 
        /// </summary>
        /// <param name="invocation">Intercepted invocation details</param>
        /// <exception cref="RemotingException">Thrown if a remoting operation has been failed</exception>
        /// <exception cref="NotSupportedException">Thrown if a member of a type marked as OneWay is intercepted, that has another return type than void</exception>
        /// <exception cref="RemoteInvocationException">Thrown if an exception occurred when the remote method was invoked</exception>
        protected override void Intercept(IInvocation invocation)
        {
            var method = invocation.Method;
            var oneWay = method.GetCustomAttribute<OneWayAttribute>() != null;
            var returnType = method.ReturnType;

            if (oneWay && returnType != typeof(void))
                throw new NotSupportedException("OneWay methods must not have a return type.");
            
            var arguments = MapArguments(invocation.Arguments);

            var remoteMethodCallMessage =
                _client.MethodCallMessageBuilder.BuildMethodCallMessage(
                    serializer: _client.Serializer,
                    remoteServiceName: _serviceName,
                    targetMethod: method,
                    args: arguments);

            var sendTask = 
                _client.InvokeRemoteMethod(remoteMethodCallMessage, oneWay);

            sendTask.Wait();

            var clientRpcContext = sendTask.Result;
            
            if (clientRpcContext.Error)
            {
                if (clientRpcContext.RemoteException == null)
                    throw new RemoteInvocationException();

                throw clientRpcContext.RemoteException;
            }

            var resultMessage = clientRpcContext.ResultMessage;

            if (resultMessage == null)
            {
                invocation.ReturnValue = null;
                return;
            }

            var parameterInfos = method.GetParameters();

            foreach (var outParameterValue in resultMessage.OutParameters)
            {
                var parameterInfo =
                    parameterInfos.First(p => p.Name == outParameterValue.ParameterName);

                invocation.Arguments[parameterInfo.Position] =
                    outParameterValue.IsOutValueNull
                        ? null
                        : outParameterValue.OutValue is Envelope outParamEnvelope
                            ? outParamEnvelope.Value
                            : outParameterValue.OutValue;
            }

            invocation.ReturnValue =
                resultMessage.IsReturnValueNull
                    ? null
                    : resultMessage.ReturnValue is Envelope returnValueEnvelope
                        ? returnValueEnvelope.Value
                        : resultMessage.ReturnValue;

            CallContext.RestoreFromSnapshot(resultMessage.CallContextSnapshot);
        }

        /// <summary>
        /// Intercepts a asynchronous call of a member on the proxy object. 
        /// </summary>
        /// <param name="invocation">Intercepted invocation details</param>
        /// <returns>Asynchronous running task</returns>
        /// <exception cref="RemotingException">Thrown if a remoting operation has been failed</exception>
        /// <exception cref="NotSupportedException">Thrown if a member of a type marked as OneWay is intercepted, that has another return type than void</exception>
        /// <exception cref="RemoteInvocationException">Thrown if an exception occurred when the remote method was invoked</exception>
        protected override async ValueTask InterceptAsync(IAsyncInvocation invocation)
        {
            var method = invocation.Method;
            var oneWay = method.GetCustomAttribute<OneWayAttribute>() != null;
            var returnType = method.ReturnType;

            if (oneWay && returnType != typeof(void))
                throw new NotSupportedException("OneWay methods must not have a return type.");
            
            var arguments = MapArguments(invocation.Arguments);

            var remoteMethodCallMessage =
                _client.MethodCallMessageBuilder.BuildMethodCallMessage(
                    serializer: _client.Serializer,
                    remoteServiceName: _serviceName,
                    targetMethod: method,
                    args: arguments);

            var clientRpcContext = 
                await _client.InvokeRemoteMethod(remoteMethodCallMessage, oneWay);
            
            if (clientRpcContext.Error)
            {
                if (clientRpcContext.RemoteException == null)
                    throw new RemoteInvocationException();

                throw clientRpcContext.RemoteException;
            }

            var resultMessage = clientRpcContext.ResultMessage;

            if (resultMessage == null)
            {
                invocation.Result = null;
                return;
            }

            invocation.Result =
                resultMessage.IsReturnValueNull
                    ? null
                    : resultMessage.ReturnValue is Envelope returnValueEnvelope
                        ? returnValueEnvelope.Value
                        : resultMessage.ReturnValue;

            CallContext.RestoreFromSnapshot(resultMessage.CallContextSnapshot);
        }
       
        /// <summary>
        /// Maps a delegate argument into a serializable RemoteDelegateInfo object.
        /// </summary>
        /// <param name="argumentType">Type of argument to be mapped</param>
        /// <param name="argument">Argument to be wrapped</param>
        /// <param name="mappedArgument">Out: Mapped argument</param>
        /// <returns>True if mapping applied, otherwise false</returns>
        private bool MapDelegateArgument(Type argumentType, object argument, out object mappedArgument)
        {
            if (argumentType == null || !typeof(Delegate).IsAssignableFrom(argumentType))
            {
                mappedArgument = argument;
                return false;
            }

            var delegateReturnType = argumentType.GetMethod("Invoke")?.ReturnType;

            if (delegateReturnType != typeof(void))
                throw new NotSupportedException("Only void delegates are supported.");

            var remoteDelegateInfo =
                new RemoteDelegateInfo(
                    handlerKey: _client.ClientDelegateRegistry.RegisterClientDelegate((Delegate)argument, this),
                    delegateTypeName: argumentType.FullName);

            mappedArgument = remoteDelegateInfo;
            return true;
        }

        /// <summary>
        /// Maps a Linq expression argument into a serializable ExpressionNode object.
        /// </summary>
        /// <param name="argumentType">Type of argument to be mapped</param>
        /// <param name="argument">Argument to be wrapped</param>
        /// <param name="mappedArgument">Out: Mapped argument</param>
        /// <returns>True if mapping applied, otherwise false</returns>
        private bool MapLinqExpressionArgument(Type argumentType, object argument, out object mappedArgument)
        {
            var isLinqExpression =
                argumentType is
                {
                    IsGenericType: true,
                    BaseType: { IsGenericType: true }
                } && argumentType.BaseType.GetGenericTypeDefinition() == typeof(Expression<>);

            if (!isLinqExpression)
            {
                mappedArgument = argument;
                return false;
            }

            var expression = (Expression)argument;
            mappedArgument = expression.ToExpressionNode();

            return true;
        }

        /// <summary>
        /// Maps non serializable arguments into a serializable form.
        /// </summary>
        /// <param name="arguments">Arguments</param>
        /// <returns>Array of arguments (includes mapped ones)</returns>
        private object[] MapArguments(IEnumerable<object> arguments)
        {
            var mappedArguments =
                arguments.Select(argument =>
                {
                    var type = argument?.GetType();

                    if (MapDelegateArgument(type, argument, out var mappedArgument))
                        return mappedArgument;

                    if (MapLinqExpressionArgument(type, argument, out mappedArgument))
                        return mappedArgument;

                    return argument;
                }).ToArray();
            
            return mappedArguments;
        }
    }
}