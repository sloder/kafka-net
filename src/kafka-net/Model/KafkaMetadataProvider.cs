﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KafkaNet.Protocol;

namespace KafkaNet.Model
{
    public class KafkaMetadataProvider : IDisposable
    {
        private const int BackoffMilliseconds = 100;
        private readonly KafkaOptions _kafkaOptions;
        private bool _interrupted;

        public KafkaMetadataProvider(KafkaOptions kafkaOptions)
        {
            _kafkaOptions = kafkaOptions;
        }

        public MetadataResponse Get(IKafkaConnection[] connections, IEnumerable<string> topics)
        {
            var request = new MetadataRequest { Topics = topics.ToList() };
            if (request.Topics.Count <= 0) return null;

            var performRetry = false;
            var retryAttempt = 0;
            MetadataResponse metadataResponse = null;

            do
            {
                performRetry = false;
                metadataResponse = GetMetadataResponse(connections, request);
                if (metadataResponse == null) return null;

                foreach (var validation in ValidateResponse(metadataResponse))
                {
                    switch (validation.Status)
                    {
                        case ValidationResult.Retry:
                            performRetry = true;
                            _kafkaOptions.Log.WarnFormat(validation.Message);
                            break;
                        case ValidationResult.Error:
                            throw validation.Exception;
                    }
                }

                BackoffOnRetry(++retryAttempt, performRetry);

            } while (_interrupted == false && performRetry);

            return metadataResponse;
        }

        private void BackoffOnRetry(int retryAttempt, bool performRetry)
        {
            if (performRetry && retryAttempt > 0)
            {
                var backoff = retryAttempt*retryAttempt*BackoffMilliseconds;
                _kafkaOptions.Log.WarnFormat("Backing off metadata request retry.  Waiting for {0}ms.", backoff);
                Thread.Sleep(TimeSpan.FromMilliseconds(backoff));
            }
        }

        private MetadataResponse GetMetadataResponse(IEnumerable<IKafkaConnection> connections, MetadataRequest request)
        {
            //try each default broker until we find one that is available
            foreach (var conn in connections)
            {
                try
                {
                    var response = conn.SendAsync(request).Result;
                    if (response != null && response.Count > 0)
                    {
                        return response.FirstOrDefault();
                    }
                }
                catch (Exception ex)
                {
                    _kafkaOptions.Log.WarnFormat("Failed to contact Kafka server={0}.  Trying next default server.  Exception={1}", conn.Endpoint, ex);
                }
            }

            throw new ServerUnreachableException(
                        "Unable to query for metadata from any of the default Kafka servers.  At least one provided server must be available.  Server list: {0}",
                        string.Join(", ", _kafkaOptions.KafkaServerUri.Select(x => x.ToString())));
        }

        private IEnumerable<MetadataValidationResult> ValidateResponse(MetadataResponse metadata)
        {
            foreach (var broker in metadata.Brokers)
            {
                yield return ValidateBroker(broker);
            }

            foreach (var topic in metadata.Topics)
            {
                yield return ValidateTopic(topic);
            }
        }

        private MetadataValidationResult ValidateBroker(Broker broker)
        {
            if (broker.BrokerId == -1)
            {
                return new MetadataValidationResult { Status = ValidationResult.Retry, ErrorCode = ErrorResponseCode.Unknown };
            }

            if (string.IsNullOrEmpty(broker.Host))
            {
                return new MetadataValidationResult
                {
                    Status = ValidationResult.Error,
                    Exception = new InvalidTopicMetadataException(ErrorResponseCode.NoError, "Broker missing host information.")
                };
            }

            if (broker.Port <= 0)
            {
                return new MetadataValidationResult
                {
                    Status = ValidationResult.Error,
                    Exception = new InvalidTopicMetadataException(ErrorResponseCode.NoError, "Broker missing port information.")
                };
            }

            return new MetadataValidationResult();
        }

        private MetadataValidationResult ValidateTopic(Topic topic)
        {
            try
            {
                var errorCode = (ErrorResponseCode)topic.ErrorCode;

                if (errorCode == ErrorResponseCode.NoError) return new MetadataValidationResult();

                switch (errorCode)
                {
                    case ErrorResponseCode.LeaderNotAvailable:
                    case ErrorResponseCode.OffsetsLoadInProgressCode:
                    case ErrorResponseCode.ConsumerCoordinatorNotAvailableCode:
                        return new MetadataValidationResult
                        {
                            Status = ValidationResult.Retry,
                            ErrorCode = errorCode,
                            Message = string.Format("Topic:{0} returned error code of {1}.  Retrying.", topic.Name, errorCode)
                        };
                }

                return new MetadataValidationResult
                {
                    Status = ValidationResult.Error,
                    ErrorCode = errorCode,
                    Exception = new InvalidTopicMetadataException(errorCode, "Topic:{0} returned an error of {1}.", topic.Name, errorCode)
                };
            }
            catch
            {
                return new MetadataValidationResult
                {
                    Status = ValidationResult.Error,
                    ErrorCode = ErrorResponseCode.Unknown,
                    Exception = new InvalidTopicMetadataException(ErrorResponseCode.Unknown, "Unknown error code returned in metadata response.  ErrorCode: {0}", topic.ErrorCode)
                };
            }
        }

        public void Dispose()
        {
            _interrupted = true;
        }
    }

    public enum ValidationResult { Valid, Error, Retry }
    public class MetadataValidationResult
    {
        public ValidationResult Status { get; set; }
        public string Message { get; set; }
        public ErrorResponseCode ErrorCode { get; set; }
        public Exception Exception { get; set; }

        public MetadataValidationResult()
        {
            ErrorCode = ErrorResponseCode.NoError;
            Status = ValidationResult.Valid;
            Message = "";
        }
    }
}
