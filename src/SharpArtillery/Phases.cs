using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SharpArtillery.Reporting;

namespace SharpArtillery
{
    /* 
     * TODO:
     *  - Add Load testing variables, things that ppl use when load-testing
     *      Response time - the amount of time between a request and a response
     *          just save the latency for each request
     *      Average Load time - the average response time
     *          Like before, just save the latency
     *      Peak response time - the longest response time
     *          nothing special here
     *      Throughput / Requests per second (rps) - number of requests handled per second
     *          Send all requests at start of second, and when second is up
     *          check the tasks of how many requests that are finished
     *          This one is best checked at the host (tricky)
     *      Error rate - errors / request ratio
     *          easy
     *      
     */
    internal static class Phases
    {
        internal static async Task RunPhases(Stopwatch timer, ArtilleryConfig flags, HttpClient client,
            List<Task<Data>> tasks,
            List<Data> completedTasks)
        {
            Debug.Assert(flags.YamlConfig.Settings != null);
            Debug.Assert(flags.YamlConfig.Settings.Phases != null);
            // yaml phases
            timer.Start();
            foreach (var phase in flags.YamlConfig.Settings.Phases)
            {
                // FEATURE: make it possible to run different scenarios
                var endpoint = flags.YamlConfig.Settings.Target + flags.YamlConfig.Scenarios[0].Flow[0].Get.Url;
                await RunPhase(endpoint, client, tasks, completedTasks, phase.Duration,
                    phase.ArrivalRate, phase.Name, phase.RampUp);
            }
            // complete any remaining tasks
            await HangBackAndDoSomeOtherWork(null, tasks, completedTasks, CancellationToken.None);

            timer.Stop();
        }

        private static async Task HangBackAndDoSomeOtherWork(Stopwatch? timer, List<Task<Data>> tasks,
            List<Data> completedRequests,
            CancellationToken ct)
        {
            // TODO: hardcoded value
            while (timer is { IsRunning:true, ElapsedMilliseconds: < 1000 } ||
                   timer == null && tasks.Count > 0)
            {
                // FEATURE: could be nice to see how many tasks are in state: WAITING_TO_RUN
                // BUG: Have seen problems with blockingList being used when in the where clause
                var requestsThatNoLongerAreRunning = tasks.Where(t => t.IsCompletedSuccessfully)
                    .ToArray();
                // t.Status is TaskStatus.Canceled or TaskStatus.Faulted or TaskStatus.RanToCompletion).ToList();
                if (requestsThatNoLongerAreRunning.Length > 0)
                {
                    for (var i = requestsThatNoLongerAreRunning.Length - 1; i >= 0; i--)
                    {
                        var requestData = await requestsThatNoLongerAreRunning[i];
                        completedRequests.Add(requestData);

                        tasks.Remove(requestsThatNoLongerAreRunning[i]);
                    }
                }

                await Task.Delay(1);
            }
        }

        internal static async Task RunPhase(string endpoint, HttpClient client, List<Task<Data>> tasks,
            List<Data> completedTasks, int duration, int arrivalRate, string phaseName, int? rampUp)
        {
            // TODO: Refactor
            var requestRate = arrivalRate;
            var name = phaseName;
            // if we have a rampUp value, just take that and divide over duration to know how many more requests per second
            var increaseRate = rampUp > requestRate
                ? (rampUp.Value - requestRate) / duration
                : 0;

            ConsoleReporter.Emit($"Starting phase : {name}");

            var durationTimer = Stopwatch.StartNew();
            var oneSecondTimer = new Stopwatch();
            while (durationTimer.Elapsed.TotalSeconds < duration)
            {
                // Debug.Assert(batchSize > 0);
                
                // check if we've already passed the second here, in that case it means we can't process number of request
                // fast enough
                if (oneSecondTimer.ElapsedMilliseconds > 1000)
                {
                    // PERF: we need to make it work faster when having lots of tasks in the blockingList
                    ConsoleReporter.Emit($"NOT FAST ENOUGH CLIENT! ({oneSecondTimer.ElapsedMilliseconds - 1000} ms over)");
                }
                
                // TODO: 
                
                // only send <requestRate> of requests every second
                await HangBackAndDoSomeOtherWork(oneSecondTimer, tasks, completedTasks, CancellationToken.None);
                ConsoleReporter.Report(durationTimer, completedTasks, tasks, requestRate);

                oneSecondTimer.Restart();
                
                // prepare batches
                var batchOfRequests = new List<Task<Data>>();
                var sendTime = Stopwatch.StartNew();
                // for (var i = 0; i < requestRate; i++)
                // {
                //     tasks.Add(DoRequest(client, tasks.Count,
                //         endpoint, requestRate, phaseName));
                // }
                var requestTasks = Enumerable.Range(0, requestRate)
                    .Select<int, Func<Task<Data>>>(i => () => 
                            DoRequest(client, tasks.Count,
                                endpoint, requestRate, phaseName)
                    ).ToArray();
                // newTasks.AddRange(requestTasks);
                // send in batches
                // foreach (var item in requestTasks)
                // {
                //     newTasks.Add(item.Invoke());
                // }
                batchOfRequests.AddRange(requestTasks.Select(req => req()));
                // var skip = 0;
                // var batchSize = (int)Math.Ceiling(100f / (1000f / requestRate));
                // while (skip < requestRate)
                // {
                //     // NOTE: if this is fast enough, we could start all tasks within the first 100ms, as there is no check
                //     var batch = skip + batchSize > requestTasks.Length ? requestTasks[skip..] :
                //         requestTasks[skip..(skip+batchSize)];
                //     Debug.Assert(batch.Length > 0);
                //
                //     tasks.AddRange(batch.Select(req => req()));
                //     skip += batchSize;
                //     await Task.Delay(100);
                // }

                sendTime.Stop();
                ConsoleReporter.Emit($"Request burst time: {sendTime.ElapsedMilliseconds} ms");

                // await tasks.AddRangeAsync(newTasks);

                

                requestRate += increaseRate;
            }
        }

        static async Task<Data> DoRequest(HttpClient client, int concurrentRequest, string endpoint, int requestRate,
            string phaseName)
        {
            var startTime = DateTime.Now;
            var data = new Data
            {
                RequestSentTime = startTime,
                RequestRate = requestRate,
                PhaseName = phaseName
            };
            try
            {
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                // NOTE: this is no guarantee that a request is actually send out at this point
                // as the internal queue inside HttpClient will decide when to send a request
                var res = await client.GetAsync(endpoint, cts.Token);

                var endTime = DateTime.Now;
                var latency = endTime - startTime;
                data.Latency = latency;
                data.RequestReceivedTime = endTime;
                data.Status = res.StatusCode;
                return data;
            }
            catch (SocketException e)
            {
                data.Error = e.Message;
                data.Status = HttpStatusCode.InternalServerError;
            }
            catch (HttpRequestException e) when (e.InnerException is SocketException socketException)
            {
                data.Error = socketException.SocketErrorCode.ToString();
                data.Status = HttpStatusCode.BadGateway;
            }
            catch (HttpRequestException e)
            {
                data.Error = e.Message;
                data.Status = HttpStatusCode.ServiceUnavailable;
            }
            catch (AggregateException e)
            {
                data.Status = HttpStatusCode.InternalServerError;
                data.Error = e.Message;
            }
            catch (TaskCanceledException e)
            {
                data.Status = HttpStatusCode.RequestTimeout;
            }
            catch (Exception e)
            {
                data.Status = HttpStatusCode.InternalServerError;
                data.Error = e.Message;
            }

            return data;
        }
    }
}