﻿using System;
using System.Reflection;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Tracy.Frameworks.Common.Extends;
using ExchangeType = RabbitMQ.Client.ExchangeType;

namespace Tracy.Frameworks.RabbitMQ
{
    #region RabbitMQ.Client原生封装类
    /// <summary>
    /// RabbitMQ.Client原生封装类
    /// </summary>
    public class RabbitMQWrapper : IDisposable
    {
        #region 初始化
        //RabbitMQ建议客户端线程之间不要共用Model，至少要保证共用Model的线程发送消息必须是串行的，但是建议尽量共用Connection。
        private static readonly ConcurrentDictionary<string, IModel> ModelDic =
            new ConcurrentDictionary<string, IModel>();

        private static RabbitMQQueueAttribute _rabbitMQQueueAttribute;

        private const string RabbitMQQueueAttribute = "RabbitMQQueueAttribute";

        private static IConnection _conn;

        private static readonly object LockObj = new object();

        public RabbitMQWrapper(RabbitMQConfig config)
        {
            Open(config);
        }

        private static void Open(RabbitMQConfig config)
        {
            if (_conn != null)
            {
                return;
            }
            lock (LockObj)
            {
                if (_conn == null)
                {
                    var factory = new ConnectionFactory
                    {
                        //设置主机名
                        HostName = config.Host,

                        //设置VirtualHost
                        VirtualHost = config.VirtualHost.IsNullOrEmpty() ? "/" : config.VirtualHost,

                        //设置心跳时间
                        RequestedHeartbeat = config.HeartBeat,

                        //设置自动重连
                        AutomaticRecoveryEnabled = config.AutomaticRecoveryEnabled,

                        //重连时间
                        NetworkRecoveryInterval = config.NetworkRecoveryInterval,

                        //用户名
                        UserName = config.UserName,

                        //密码
                        Password = config.Password
                    };
                    _conn = factory.CreateConnection();
                }
            }
        }

        private static RabbitMQQueueAttribute GetRabbitMqAttribute<T>()
        {
            if (_rabbitMQQueueAttribute.IsNull())
            {
                var typeOfT = typeof(T);
                Attribute customAttribute = Attribute.GetCustomAttribute(typeOfT, typeof(RabbitMQQueueAttribute));
                if (customAttribute != null)
                {
                    _rabbitMQQueueAttribute = customAttribute as RabbitMQQueueAttribute;
                }
            }

            return _rabbitMQQueueAttribute;
        }
        #endregion

        #region 声明交换器
        /// <summary>
        /// 交换器声明
        /// </summary>
        /// <param name="iModel"></param>
        /// <param name="exchange">交换器</param>
        /// <param name="type">交换器类型：
        /// 1、Direct Exchange – 处理路由键。需要将一个队列绑定到交换机上，要求该消息与一个特定的路由键完全
        /// 匹配。这是一个完整的匹配。如果一个队列绑定到该交换机上要求路由键 “dog”，则只有被标记为“dog”的
        /// 消息才被转发，不会转发dog.puppy，也不会转发dog.guard，只会转发dog
        /// 2、Fanout Exchange – 不处理路由键。你只需要简单的将队列绑定到交换机上。一个发送到交换机的消息都
        /// 会被转发到与该交换机绑定的所有队列上。很像子网广播，每台子网内的主机都获得了一份复制的消息。Fanout
        /// 交换机转发消息是最快的。
        /// 3、Topic Exchange – 将路由键和某模式进行匹配。此时队列需要绑定要一个模式上。符号“#”匹配一个或多
        /// 个词，符号“*”匹配不多不少一个词。因此“audit.#”能够匹配到“audit.irs.corporate”，但是“audit.*”
        /// 只会匹配到“audit.irs”。</param>
        /// <param name="durable">持久化，默认为true</param>
        /// <param name="autoDelete">自动删除</param>
        /// <param name="arguments">参数</param>
        private static void ExchangeDeclare(IModel iModel, string exchange, string type = ExchangeType.Direct,
            bool durable = true,
            bool autoDelete = false, IDictionary<string, object> arguments = null)
        {
            exchange = exchange.IsNullOrWhiteSpace() ? "" : exchange.Trim();
            iModel.ExchangeDeclare(exchange, type, durable, autoDelete, arguments);
        }
        #endregion

        #region 声明队列
        /// <summary>
        /// 队列声明
        /// </summary>
        /// <param name="channel"></param>
        /// <param name="queue">队列</param>
        /// <param name="durable">持久化，默认为true</param>
        /// <param name="exclusive">排他队列，如果一个队列被声明为排他队列，该队列仅对首次声明它的连接可见，
        /// 并在连接断开时自动删除。这里需要注意三点：其一，排他队列是基于连接可见的，同一连接的不同信道是可
        /// 以同时访问同一个连接创建的排他队列的。其二，“首次”，如果一个连接已经声明了一个排他队列，其他连
        /// 接是不允许建立同名的排他队列的，这个与普通队列不同。其三，即使该队列是持久化的，一旦连接关闭或者
        /// 客户端退出，该排他队列都会被自动删除的。这种队列适用于只限于一个客户端发送读取消息的应用场景。</param>
        /// <param name="autoDelete">自动删除</param>
        /// <param name="arguments">参数</param>
        private static void QueueDeclare(IModel channel, string queue, bool durable = true, bool exclusive = false,
            bool autoDelete = false, IDictionary<string, object> arguments = null)
        {
            queue = queue.IsNullOrWhiteSpace() ? "UndefinedQueueName" : queue.Trim();
            channel.QueueDeclare(queue, durable, exclusive, autoDelete, arguments);
        }
        #endregion

        #region 获取Model

        /// <summary>
        /// 获取Model
        /// </summary>
        /// <param name="exchange">交换机名称</param>
        /// <param name="queue">队列名称</param>
        /// <param name="routingKey"></param>
        /// <param name="isProperties">是否持久化，默认为true</param>
        /// <returns></returns>
        private static IModel GetModel(string exchange, string queue, string routingKey, bool isProperties = false)
        {
            return ModelDic.GetOrAdd(queue, key =>
            {
                var lockObject = string.Intern(queue);
                lock (lockObject)
                {
                    if (ModelDic.ContainsKey(queue))
                    {
                        return ModelDic[queue];
                    }

                    var model = _conn.CreateModel();
                    ExchangeDeclare(model, exchange, ExchangeType.Direct, isProperties);
                    QueueDeclare(model, queue, isProperties);
                    model.QueueBind(queue, exchange, routingKey);
                    ModelDic[queue] = model;
                    return model;
                }
            });
        }

        /// <summary>
        /// 获取Model
        /// </summary>
        /// <param name="queue">队列名称</param>
        /// <param name="isProperties">是否持久化，默认为true</param>
        /// <returns></returns>
        private static IModel GetModel(string queue, bool isProperties = false)
        {
            return ModelDic.GetOrAdd(queue, value =>
            {
                var lockObject = string.Intern(queue);
                lock (lockObject)
                {
                    if (ModelDic.ContainsKey(queue))
                    {
                        return ModelDic[queue];
                    }

                    var model = _conn.CreateModel();
                    QueueDeclare(model, queue, isProperties);

                    //设置公平调度，每次处理一条
                    model.BasicQos(0, 1, false);

                    ModelDic[queue] = model;
                    return model;
                }
            });
        }
        #endregion

        #region 发布消息

        /// <summary>
        /// 发布消息
        /// </summary>
        /// <param name="command">指令</param>
        /// <returns></returns>
        public void Publish<T>(T command) where T : class
        {
            var queueInfo = GetRabbitMqAttribute<T>();
            if (queueInfo.IsNull())
            {
                throw new ArgumentException(RabbitMQQueueAttribute);
            }

            var body = command.ToJson();
            var exchange = queueInfo.ExchangeName;
            var queue = queueInfo.QueueName;
            var routingKey = queueInfo.QueueName;
            var isProperties = queueInfo.IsProperties;

            Publish(exchange, queue, routingKey, body, isProperties);
        }

        /// <summary>
        /// 发布消息
        /// </summary>
        /// <param name="routingKey">路由键</param>
        /// <param name="body">队列信息</param>
        /// <param name="exchange">交换机名称</param>
        /// <param name="queue">队列名</param>
        /// <param name="isProperties">是否持久化</param>
        /// <returns></returns>
        public void Publish(string exchange, string queue, string routingKey, string body, bool isProperties = false)
        {
            var channel = GetModel(exchange, queue, routingKey, isProperties);

            //如果需要持久化，消息也需要设置为持久化
            //只有交换机，队列和消息都设置为持久化时，消息才能真正的持久化，重启服务器后消息不会丢失
            var props = channel.CreateBasicProperties();
            if (isProperties)
            {
                props.Persistent = true;
            }
            channel.BasicPublish(exchange, routingKey, props, body.SerializeUtf8());
        }
        #endregion

        #region 订阅消息

        /// <summary>
        /// 接收消息
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="handler">消费处理</param>
        public void Subscribe<T>(Action<T> handler) where T : class
        {
            var queueInfo = GetRabbitMqAttribute<T>();
            if (queueInfo.IsNull())
            {
                throw new ArgumentException(RabbitMQQueueAttribute);
            }

            Subscribe(queueInfo.QueueName, queueInfo.IsProperties, handler);
        }

        /// <summary>
        /// 接收消息
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="queue">队列名称</param>
        /// <param name="isProperties"></param>
        /// <param name="handler">消费处理</param>
        /// <param name="isDeadLetter"></param>
        public void Subscribe<T>(string queue, bool isProperties, Action<T> handler) where T : class
        {
            var channel = GetModel(queue, isProperties);
            var consumer = new EventingBasicConsumer(channel);
            consumer.Received += (model, ea) =>
            {
                var body = ea.Body;
                var msgStr = body.DeserializeUtf8();
                var msg = msgStr.FromJson<T>();

                handler(msg);

                //消息确认
                channel.BasicAck(ea.DeliveryTag, false);
            };
            channel.BasicConsume(queue, false, consumer);
        }

        #endregion

        #region 获取消息

        /// <summary>
        /// 获取消息
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="handler">消费处理</param>
        public void Pull<T>(Action<T> handler) where T : class
        {
            var queueInfo = GetRabbitMqAttribute<T>();
            if (queueInfo.IsNull())
            {
                throw new ArgumentException("RabbitMqAttribute");
            }

            Pull(queueInfo.ExchangeName, queueInfo.QueueName, queueInfo.QueueName, handler);
        }

        /// <summary>
        /// 获取消息
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="exchange"></param>
        /// <param name="queue"></param>
        /// <param name="routingKey"></param>
        /// <param name="handler">消费处理</param>
        private void Pull<T>(string exchange, string queue, string routingKey, Action<T> handler) where T : class
        {
            var channel = GetModel(exchange, queue, routingKey);

            var result = channel.BasicGet(queue, false);
            if (result.IsNull())
            {
                return;
            }

            var msg = result.Body.DeserializeUtf8().FromJson<T>();

            handler(msg);

            //消息确认
            channel.BasicAck(result.DeliveryTag, false);
        }

        #endregion

        #region RPC

        #region RPC客户端
        /// <summary>
        /// RPC客户端
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="command"></param>
        /// <returns></returns>
        public string RpcClient<T>(T command) where T : class
        {
            var queueInfo = GetRabbitMqAttribute<T>();

            if (queueInfo.IsNull())
                throw new ArgumentException("RabbitMqAttribute");

            var body = command.ToJson();
            var exchange = queueInfo.ExchangeName;
            var queue = queueInfo.QueueName;
            var routingKey = queueInfo.ExchangeName;
            var isProperties = queueInfo.IsProperties;

            return RpcClient(exchange, queue, routingKey, body, isProperties);
        }

        /// <summary>
        /// RPC客户端
        /// </summary>
        /// <param name="exchange"></param>
        /// <param name="queue"></param>
        /// <param name="routingKey"></param>
        /// <param name="body"></param>
        /// <param name="isProperties"></param>
        /// <returns></returns>
        public string RpcClient(string exchange, string queue, string routingKey, string body, bool isProperties = false)
        {
            var channel = GetModel(exchange, queue, routingKey, isProperties);

            var consumer = new QueueingBasicConsumer(channel);
            channel.BasicConsume(queue, true, consumer);

            try
            {
                var correlationId = Guid.NewGuid().ToString();
                var basicProperties = channel.CreateBasicProperties();
                basicProperties.ReplyTo = queue;
                basicProperties.CorrelationId = correlationId;

                channel.BasicPublish(exchange, routingKey, basicProperties, body.SerializeUtf8());

                var sw = Stopwatch.StartNew();
                while (true)
                {
                    var ea = consumer.Queue.Dequeue();
                    if (ea.BasicProperties.CorrelationId == correlationId)
                    {
                        return ea.Body.DeserializeUtf8();
                    }

                    if (sw.ElapsedMilliseconds > 30000)
                        throw new Exception("等待响应超时");
                }
            }
            catch (Exception ex)
            {
                //throw ex.GetInnestException();
                throw;
            }
        }
        #endregion

        #region RPC服务端
        /// <summary>
        /// RPC服务端
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="handler"></param>
        public void RpcService<T>(Func<T, T> handler) where T : class
        {
            var queueInfo = GetRabbitMqAttribute<T>();
            if (queueInfo.IsNull())
            {
                throw new ArgumentException("RabbitMqAttribute");
            }

            RpcService(queueInfo.ExchangeName, queueInfo.QueueName, queueInfo.IsProperties, handler);
        }

        /// <summary>
        /// RPC服务端
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="exchange"></param>
        /// <param name="queue"></param>
        /// <param name="isProperties"></param>
        /// <param name="handler"></param>
        /// <param name="isDeadLetter"></param>
        public void RpcService<T>(string exchange, string queue, bool isProperties, Func<T, T> handler)
        {
            //队列声明
            var channel = GetModel(queue, isProperties);

            var consumer = new EventingBasicConsumer(channel);
            consumer.Received += (model, ea) =>
            {
                var body = ea.Body;
                var msgStr = body.DeserializeUtf8();
                var msg = msgStr.FromJson<T>();

                var props = ea.BasicProperties;
                var replyProps = channel.CreateBasicProperties();
                replyProps.CorrelationId = props.CorrelationId;

                try
                {
                    msg = handler(msg);
                }
                catch (Exception ex)
                {
                    //ex.GetInnestException().WriteToFile("队列接收消息", "RabbitMq");
                }
                finally
                {
                    channel.BasicPublish(exchange, props.ReplyTo, replyProps, msg.ToJson().SerializeUtf8());
                    channel.BasicAck(ea.DeliveryTag, false);
                }
            };
            channel.BasicConsume(queue, false, consumer);
        }
        #endregion

        #endregion

        #region 释放资源
        /// <summary>
        /// 执行与释放或重置非托管资源关联的应用程序定义的任务。
        /// </summary>
        public void Dispose()
        {
            foreach (var item in ModelDic)
            {
                item.Value.Dispose();
            }
            _conn.Dispose();
            _conn = null;
        }
        #endregion
    }
    #endregion
}