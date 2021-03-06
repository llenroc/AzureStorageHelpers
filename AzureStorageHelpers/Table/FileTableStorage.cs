﻿using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Text;
using System.Globalization;

namespace AzureStorageHelpers
{
    // Simulate storage on the file system
    // For local testing. 
    public class FileTableStorage<T> : ITableStorage<T> where T : TableEntity, new()
    {
        private readonly string _root;

        public FileTableStorage(string root, string tableName)
        {
            _root = Path.Combine(root, "table", tableName);
            Directory.CreateDirectory(_root);
        }

        // Lock for dealingw with contention writes. 
        // Not bullet proof (doesn't work cross-process); but good enough for local testing. 
        object _lock = new object();

        // $$$ - Respect insert mode
        public async Task WriteBatchAsync(T[] entities, TableInsertMode mode)
        {
            lock (_lock)
            {
                foreach (var entity in entities)
                {
                    WriteOneWorker(entity);
                }
            }
        }

        public async Task WriteOneAsync(T entity, TableInsertMode mode )
        {
            lock (_lock)
            {
                WriteOneWorker(entity);
            }
        }

        // https://docs.microsoft.com/en-us/rest/api/storageservices/fileservices/Understanding-the-Table-Service-Data-Model?redirectedfrom=MSDN
        // Azure table doesn't allow / \ # ? 
        // Files don't allow    /, \, ?, :, *, <,  >, | 
        // so # is a valid file escapacing characters 
        static string Escape(string key)
        {
            StringBuilder escapedStorageKey = new StringBuilder(key.Length);
            foreach (char c in key)
            {
                if (!char.IsLetterOrDigit(c))
                {
                    escapedStorageKey.Append(EscapeStorageCharacter(c));
                }
                else
                {
                    escapedStorageKey.Append(c);
                }
            }

            return escapedStorageKey.ToString();
        }

        private static string EscapeStorageCharacter(char character)
        {
            var ordinalValue = (ushort)character;
            if (ordinalValue < 0x100)
            {
                return string.Format(CultureInfo.InvariantCulture, "#{0:X2}", ordinalValue);
            }
            else
            {
                return string.Format(CultureInfo.InvariantCulture, "##{0:X4}", ordinalValue);
            }
        }

        // May throw on contention 
        private void WriteOneWorker(T entity)
        {
            string path = Path.Combine(_root, Escape(entity.PartitionKey), Escape(entity.RowKey)) + ".json";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(entity));
        }

        public async Task WriteOneMergeAsync(T entity)
        {
            lock (_lock)
            {
                var existing = LookupOneWorker(entity.PartitionKey, entity.RowKey);
                if (existing == null)
                {
                    WriteOneWorker(entity);
                    return; // success
                }

                // Merge in. 
                foreach (var prop in typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    var val = prop.GetValue(entity);
                    if (val != null)
                    {
                        prop.SetValue(existing, val);
                    }
                }
                WriteOneWorker(existing);
                return; // success
            }                
        }

        public async Task DeleteOneAsync(T entity)
        {
            if (entity.ETag == null)
            {
                throw new InvalidOperationException("Delete requires an Etag. Can use '*'.");
            }
            string path = Path.Combine(_root, Escape(entity.PartitionKey), Escape(entity.RowKey)) + ".json";

            string currentEtag = new FileInfo(path).LastWriteTimeUtc.ToString();
            if (entity.ETag != "*" && entity.ETag != currentEtag)
            {
                throw new InvalidOperationException("Etag mismatch"); // Todo - give real error (429?)
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public async Task<T> LookupOneAsync(string partitionKey, string rowKey)
        {
            return LookupOneWorker(partitionKey, rowKey);
        }

        private T LookupOneWorker(string partitionKey, string rowKey)
        {
            string path = Path.Combine(_root, Escape(partitionKey), Escape(rowKey)) + ".json";
            if (File.Exists(path))
            {
                return ReadEntity(path);
            }
            return null;
        }

        private static T ReadEntity(string path)
        {
            string json = File.ReadAllText(path);
            var obj = JsonConvert.DeserializeObject<T>(json);
            obj.Timestamp = new FileInfo(path).LastWriteTimeUtc;
            obj.ETag = new FileInfo(path).LastWriteTimeUtc.ToString();
            return obj;
        }

        public async Task<Segment<T>> LookupAsync(
            string partitionKey, 
            string rowKeyStart, 
            string rowKeyEnd,
            string continuationToken)
        {
            string[] paths;
            if (partitionKey == null)
            {
                // Return all
                paths = Directory.EnumerateDirectories(_root).ToArray();
            }
            else
            {
                string path = Path.Combine(_root, Escape(partitionKey));
                Directory.CreateDirectory(path);
                paths = new string[] { path };
            }

            if (rowKeyStart != null)
            {
                rowKeyStart = Escape(rowKeyStart);
            }
            if (rowKeyEnd != null)
            {
                rowKeyEnd = Escape(rowKeyEnd);
            }

            List<T> l = new List<T>();
            foreach (var path in paths)
            {
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    string rowKey = Path.GetFileNameWithoutExtension(file);

                    if (rowKeyStart != null)
                    {
                        if (string.Compare(rowKey, rowKeyStart) < 0)
                        {
                            continue;
                        }
                    }
                    if (rowKeyEnd != null)
                    {
                        if (string.Compare(rowKey, rowKeyEnd) > 0)
                        {
                            continue;
                        }
                    }

                    l.Add(ReadEntity(file));
                }
            }

            // Excercise continuation tokens.  
            if (continuationToken == null)
            {
                if (l.Count == 0)
                {
                    return new Segment<T>(new T[0], null);
                }
                return new Segment<T>(new T[1] { l[0] }, "x");
            }
            else if (continuationToken == "x")
            {
                return new Segment<T>(l.Skip(1).ToArray());
            }
            else
            {
                throw new InvalidOperationException("illegal continuation token");
            }
        }    
    }
}