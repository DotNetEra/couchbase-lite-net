//
// MultipartDocumentReader.cs
//
// Author:
//     Zachary Gramana  <zack@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc
// Copyright (c) 2014 .NET Foundation
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
//
// Copyright (c) 2014 Couchbase, Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file
// except in compliance with the License. You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software distributed under the
// License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND,
// either express or implied. See the License for the specific language governing permissions
// and limitations under the License.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Couchbase.Lite;
using Couchbase.Lite.Support;
using Couchbase.Lite.Util;
using Sharpen;

namespace Couchbase.Lite.Support
{
    internal class MultipartDocumentReader : IMultipartReaderDelegate
    {
        private MultipartReader multipartReader;

        private BlobStoreWriter curAttachment;

        private List<Byte> jsonBuffer;

        private IDictionary<String, Object> document;

        private Database database;

        private IDictionary<String, BlobStoreWriter> attachmentsByName;

        private IDictionary<String, BlobStoreWriter> attachmentsBySHA1Digest;

        public MultipartDocumentReader(Database database)
        {
            this.database = database;
        }

        public IDictionary<String, Object> GetDocumentProperties()
        {
            return document;
        }

        public void ParseJsonBuffer()
        {
            try {
                document = Manager.GetObjectMapper().ReadValue<IDictionary<String, Object>>(jsonBuffer.ToArray());
            } catch (IOException e) {
                throw new InvalidOperationException("Failed to parse json buffer", e);
            } catch(CouchbaseLiteException e) {
                throw new InvalidOperationException("Failed to parse json buffer", e);
            }
            jsonBuffer = null;
        }

        public void SetContentType(String contentType)
        {
            if (contentType == null 
                || contentType.StartsWith("application/json", StringComparison.Ordinal)
                || contentType.StartsWith("text/plain", StringComparison.Ordinal)) {
                // No multipart, so no attachments. Body is pure JSON. (We allow text/plain because CouchDB
                // sends JSON responses using the wrong content-type.)
                jsonBuffer = new List<byte>();
            } else if (contentType.StartsWith ("multipart/", StringComparison.InvariantCultureIgnoreCase)) {
                multipartReader = new MultipartReader(contentType, this);
                attachmentsByName = new Dictionary<String, BlobStoreWriter>();
                attachmentsBySHA1Digest = new Dictionary<String, BlobStoreWriter>();
            }  else {
                throw new ArgumentException("contentType must start with multipart/");
            }
        }

        public void AppendData(IEnumerable<byte> data)
        {
            if (multipartReader != null)
            {
                multipartReader.AppendData(data);
            }
            else
            {
                jsonBuffer.AddRange(data);
            }
        }

        public void Finish()
        {
            if (multipartReader != null)
            {
                if (!multipartReader.Finished())
                {
                    throw new InvalidOperationException("received incomplete MIME multipart response");
                }
                RegisterAttachments();
            }
            else
            {
                ParseJsonBuffer();
            }
        }

        private void RegisterAttachments()
        {
            var numAttachmentsInDoc = 0;

            var attachments = document.Get("_attachments").AsDictionary<string, object>();
            if (attachments == null)
            {
                return;
            }

            var nuAttachments = new Dictionary<string, object>(attachments.Count);
            foreach (var attmt in attachments)
            {
                var attachmentName = attmt.Key;
                var attachment = attmt.Value.AsDictionary<string, object>();

                long length = 0;
                var lengthValue = attachment.Get("length");
                if (lengthValue != null)
                {
                    length = (long)lengthValue;
                }
                var encodedLengthValue = attachment.Get("encoded_length");
                if (encodedLengthValue != null)
                {
                    length = (long)encodedLengthValue;
                }

                var followsValue = attachment.GetCast<bool>("follows");
                if (followsValue)
                {
                    var digest = attachment.GetCast<string>("digest");
                    var writer =  attachmentsByName.Get(attachmentName);
                    if (writer != null)
                    {
                        // Identified the MIME body by the filename in its Disposition header:
                        var actualDigest = writer.SHA1DigestString();
                        if (digest != null && !digest.Equals(actualDigest) && !digest.Equals(writer.MD5DigestString()))
                        {
                            var errMsg = String.Format("Attachment '{0}' has incorrect MD5 digest ({1}; should be either {2} or {3})", attachmentName, digest, actualDigest, writer.MD5DigestString());
                            throw new InvalidOperationException(errMsg);
                        }

                        attachment["digest"] = actualDigest;
                    }
                    else
                    {
                        if (digest != null)
                        {
                            writer = attachmentsBySHA1Digest.Get(digest);
                            if (writer == null)
                            {
                                var errMsg = String.Format("Attachment '{0}' does not appear in MIME body ", attachmentName);
                                throw new InvalidOperationException(errMsg);
                            }
                        }
                        else
                        {
                            if (attachments.Count == 1 && attachmentsBySHA1Digest.Count == 1)
                            {
                                // Else there's only one attachment, so just assume it matches & use it:
                                writer = attachmentsBySHA1Digest.Values.First();
                                attachment["digest"] = writer.SHA1DigestString();
                            }
                            else
                            {
                                // No digest metatata, no filename in MIME body; give up:
                                var errMsg = String.Format("Attachment '{0}' has no digest metadata; cannot identify MIME body", attachmentName);
                                throw new InvalidOperationException(errMsg);
                            }
                        }
                    }

                    // Check that the length matches:
                    if (writer.GetLength() != length)
                    {
                        var errMsg = String.Format("Attachment '{0}' has incorrect length field {1} (should be {2})", attachmentName, length, writer.GetLength());
                        throw new InvalidOperationException(errMsg);
                    }

                    nuAttachments[attachmentName] = attachment;
                    ++numAttachmentsInDoc;
                }
                else
                {
                    if (attachment.Get("data") != null && length > 1000)
                    {
                        var msg = String.Format("Attachment '{0}' sent inline (len={1}).  Large attachments "
                            + "should be sent in MIME parts for reduced memory overhead.", attachmentName);
                        Log.W(Database.TAG, msg);
                    }
                }
            }

            if (numAttachmentsInDoc < attachmentsBySHA1Digest.Count)
            {
                var msg = String.Format("More MIME bodies ({0}) than attachments ({1}) ", attachmentsBySHA1Digest.Count, numAttachmentsInDoc);
                throw new InvalidOperationException(msg);
            }

            document["_attachments"] = nuAttachments;
            // hand over the (uninstalled) blobs to the database to remember:
            database.RememberAttachmentWritersForDigests(attachmentsBySHA1Digest);
        }

        public void StartedPart(IDictionary<String, String> headers)
        {
            if (document == null)
            {
                jsonBuffer = new List<Byte>(1024);
            }
            else
            {
                curAttachment = database.AttachmentWriter;
                var contentDisposition = headers.Get("Content-Disposition");
                if (contentDisposition != null && contentDisposition.StartsWith("attachment; filename="))
                {
                    // TODO: Parse this less simplistically. Right now it assumes it's in exactly the same
                    // format generated by -[CBL_Pusher uploadMultipartRevision:]. CouchDB (as of 1.2) doesn't
                    // output any headers at all on attachments so there's no compatibility issue yet.
                    var contentDispositionUnquoted = Misc.UnquoteString(contentDisposition);
                    var name = contentDispositionUnquoted.Substring(21);
                    if (name != null)
                    {
                        attachmentsByName.Put(name, curAttachment);
                    }
                }
            }
        }

        public void AppendToPart(IEnumerable<Byte> data)
        {
            if (jsonBuffer != null)
            {
                jsonBuffer.AddRange(data);
            }
            else
            {
                curAttachment.AppendData(data.ToArray());
            }
        }

        public void FinishedPart()
        {
            if (jsonBuffer != null)
            {
                ParseJsonBuffer();
            }
            else
            {
                curAttachment.Finish();
                String sha1String = curAttachment.SHA1DigestString();
                attachmentsBySHA1Digest.Put(sha1String, curAttachment);
                curAttachment = null;
            }
        }
    }
}
