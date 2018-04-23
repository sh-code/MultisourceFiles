/*
 * README!!!!!!!
 * In current form, this code is dependent on UnityEngine.dll version 5.5 and won't even compile without it, but the fix is relatively easy:
 * all that's needed is to replace WWW and WWWForm with non-unity classes/remote loading, and remove/modify MultisourceImage to return something more generic than UnityEngine.Texture2D
 * 
 * Logging has been disabled, because the code for it was custom-made and I can't find it right now
 * 
 * For now, this code is provided "as-is", and while you're welcome to give me suggestions for improvement and expansion, don't expect me to use them by default and you're probably better
 * off implementing suggested functionality yourself
 * 
 * M. Martinovič - sh code - twitter: @semihybrid
 */

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.IO;
using System.Xml;
using System.Threading;
using System;
//using shc.Logging;
using System.Xml.Linq;

namespace shc
{
	public static class MultisourceFiles
	{
		public static MultisourceXml GetRssSource(string name, string url)
		{
			MultisourceXml res = new MultisourceXml(
				new SourcePath(name + ".xml", true, true),
				new SourcePath(url)
			);

			return res;
		}

		public static MultisourceXml GetPlaylistSource(string url, string firma, string pobocka)
		{
			MultisourceXml res = new MultisourceXml(
				new SourcePath("playlist.xml", true, true),
				new SourcePath(url, new Dictionary<string, string>() {
				{ "action", "pl" },
				{ "firma", firma },
				{ "pobocka", pobocka }}
				)
			);

			return res;
		}

		public static MultisourceXml GetConfigSource()
		{
			MultisourceXml res = new MultisourceXml(
				new SourcePath("config.xml", true));

			return res;
		}
	}

	public class SourcePath
	{
		public static string defaultStorageRoot
		{
			get { return _defaultStorageRoot + "/"; }
			set { _defaultStorageRoot = value; }
		}
		private static string _defaultStorageRoot = "";
		public string path;
		public bool isLocal;
		public bool isSyncTarget;
		protected Dictionary<string, string> post;
		public bool hasPostData { get { return (post != null) && (post.Count > 0); } }
		public Dictionary<string, string> getPostData() { return post; }

		/// <summary>
		/// 
		/// </summary>
		/// <param name="path">absolute path/url if path is not local, filename relative to defaultStorageRoot if path is local</param>
		/// <param name="isLocal"></param>
		/// <param name="isSyncTarget">should we try to write/cache/sync the most current data to this location?</param>
		public SourcePath(string path, Dictionary<string, string> postData = null, bool isLocal = false, bool isSyncTarget = false)
		{
			this.path = path; this.isLocal = isLocal; this.isSyncTarget = isSyncTarget;

			if (isLocal)//disk access, prefix app root, postfix file extension
			{
				this.path = SourcePath.defaultStorageRoot + path;
			}
			else //remote access, completely custom, absolute address
			{
				this.path = path;
				post = postData;
			}
		}

		public SourcePath(string path, bool isLocal, bool isSyncTarget = false)
		{
			this.path = path; this.isLocal = isLocal; this.isSyncTarget = isSyncTarget;

			if (isLocal)//disk access, prefix app root, postfix file extension
			{
				this.path = SourcePath.defaultStorageRoot + path;
			}
			else //remote access, completely custom, absolute address
			{
				this.path = path;
			}
		}
	}

	public class MultisourceFile
	{
		public enum eContentType
		{
			Unknown, Text, Xml, Image, Video, Audio
		}
		public string fileName
		{
			get { return _fileName; }
		}
		private string _fileName;

		protected eContentType contentType = eContentType.Unknown;
		public eContentType ContentType { get { return contentType; } }
		protected List<SourcePath> sourcePaths;
		protected byte[] file, cachedFile;
		public byte[] bytes
		{
			get { return file; }
			set { file = value; }
		}
		public virtual byte[] content
		{
			get { return file; }
		}
		//can be overriden in derived to check their type of content specifically
		public virtual bool isContentReady { get { return bytes != null; } }

		public MultisourceFile(params SourcePath[] sources)
		{
			sourcePaths = new List<SourcePath>(sources);
			_fileName = sourcePaths[0].path;
		}

		public MultisourceFile(XElement sourceXml)
		{
			XElement s = sourceXml;
			//todo: don't ignore refresh interval
			//can have multiple local and remote sources, need to take them in order
			sourcePaths = new List<SourcePath>();

			try
			{
				foreach (XElement p in s.Elements().InDocumentOrder())
				{
					if (p.Name == "local") //automatically a relative path
					{
						sourcePaths.Add(new SourcePath(p.Element("path").Value.Trim(), true, (bool)p.Attribute("isSyncTarget")));
					}
					else if (p.Name == "remote") //needs to be absolute
					{
						Dictionary<string, string> post;

						if (p.Element("postData") != null)
						{
							post = new Dictionary<string, string>();

							foreach (XElement pd in p.Element("postData").Elements("item"))
							{
								post.Add(pd.Attribute("name").Value, pd.Value.Trim());
							}

							sourcePaths.Add(new SourcePath(p.Element("path").Value.Trim(), post));
						}
					}
				}
			}
			catch (Exception e)
			{
				Debug.Log(e.ToString());
			}

			_fileName = sourcePaths[0].path;
		}

		public void Load(bool syncTargets = true)
		{
			//goes through the sources. first one it finds as valid is downloaded and opened
			//then all the isLocal && isSyncTarget sources are saved into (if syncTargets)
			int firstValidSource = -1;
			for (int c = 0; !isContentReady && (c < sourcePaths.Count); c++)
			{
				//l.w("(MSF)Try load source " + sourcePaths[c].path);
				if (TryLoadSource(sourcePaths[c]))
				{
					break;
				}
			}

			if (!isContentReady) throw new Exception("(MSF)No valid source loaded");

			//sync all isSyncTarget sources according to the first valid source
			for (int c = 0; c < sourcePaths.Count; c++)
			{
				if (sourcePaths[c].isSyncTarget) SyncSource(sourcePaths[c], file);
			}
		}

		//todo: this needs to run on separate thread probably
		private bool TryLoadSource(SourcePath p)
		{
			try
			{
				WWWForm post = null;
				WWW request = null;

				try
				{
					if (!p.isLocal)
					{
						if (p.hasPostData)
						{
							post = new WWWForm();
							foreach (var field in p.getPostData())
							{
								post.AddField(field.Key, field.Value);
							}
						}

						//l.w("(MSF)...isLocal == FALSE");

						if (post != null)
						{
							//l.w("(MSF)...hasPost == TRUE. Making GET request");
							request = new WWW(p.path, post);
						}
						else
						{
							//l.w("(MSF)...hasPost == FALSE. Making POST request");
							request = new WWW(p.path);
						}

						//w.InitWWW(url, null, new string[] { "Request-Encoding: utf-8" });
						while (request.isDone == false) Thread.Sleep(100);

						//l.w("(MSF)...source response recieved");

						//l.w("(MSF)Content-Length: " + request.responseHeaders["Content-Length"]);
						//l.w("(MSF)Content-length: " + request.responseHeaders["Content-length"]);
						//l.w("(MSF)content-length: " + request.responseHeaders["content-length"]);
						file = request.bytes;
						RecieveTryLoadSource(request);
						//l.w("...MSF loaded and parsed");
						return true;

					}
					else
					{
						//l.w("(MSF)...isLocal == TRUE");
						if (File.Exists(p.path))
						{
							file = File.ReadAllBytes(p.path);
							using (FileStream src = File.OpenRead(p.path))
							{
								RecieveTryLoadSource(src);
							}

							//l.w("...MSF loaded and parsed from " + p.path);
						}

						bytes = file;

					}
				}
				catch (Exception e)
				{
					Debug.Log("(MFS)Failed. Error: " + e.ToString());
				}

			}
			catch (Exception e)
			{
				//todo: log the exception somewhere useful
				Debug.Log(e.ToString());
				return false;
			}

			return false;
		}

		protected virtual bool SyncSource(SourcePath target, byte[] data)
		{
			return SyncSource(new SourcePath[] { target }, data);
		}

		protected virtual bool SyncSource(SourcePath[] target, byte[] data)
		{
			try
			{
				for (int c = 0; c < target.Length; c++)
				{
					//todo: later we could theoretically sync even non-locals
					if (!target[c].isLocal) throw new System.Exception("(MSF)SyncSource: Target[" + c + "] SourcePath (" + target[c].path + ") must be local!");


					File.WriteAllBytes(target[0].path, data);
				}
			}
			catch (Exception e)
			{
				return false;
			}

			return true;
		}

		protected virtual void RecieveTryLoadSource(WWW request)
		{
			//filling bytes is handled by the main code, this is only to provide
			//custom loading for derived classes
		}

		protected virtual void RecieveTryLoadSource(Stream src)
		{
			//filling bytes is handled by the main code, this is only to provide
			//custom loading for derived classes
		}
	}

	public class MultisourceImage : MultisourceFile
	{
		private Texture2D texture;
		public new Texture2D content
		{
			get { return texture; }
			set { texture = value; }
		}
		public new bool isContentReady { get { return content != null; } }

		public MultisourceImage(params SourcePath[] sources)
		: base(sources) { }

		protected override void RecieveTryLoadSource(WWW request)
		{
			try
			{
				texture = request.texture;
			}
			catch (Exception e)
			{
				//what does it throw when there was no texture in response?
				throw e;
			}

			contentType = eContentType.Image;
		}

		protected override void RecieveTryLoadSource(Stream src)
		{
			try
			{
				byte[] data = new byte[src.Length];
				src.Read(data, 0, data.Length);
				texture.LoadImage(data);
			}
			catch (Exception e)
			{
				//todo: we can get byte read error or loadimage error
				//proper ways to handle them
			}

			contentType = eContentType.Image;
		}
	}

	public class MultisourceXml : MultisourceFile
	{
		private XDocument xml, cachedXml;
		public new XDocument content
		{
			get { return xml; }
			set { xml = value; }
		}

		public new bool isContentReady
		{
			get
			{
				return xml != null;
			}
		}

		public MultisourceXml(params SourcePath[] sources)
			: base(sources) { }

		public MultisourceXml(XElement sourceXml)
			: base(sourceXml) { }

		protected override void RecieveTryLoadSource(WWW request)
		{
			try
			{
				xml = XDocument.Parse(request.text);
				//l.w("(MSXML)Xml text parsed");
			}
			catch (Exception e)
			{
				//todo
			}

			contentType = eContentType.Xml;
		}

		protected override void RecieveTryLoadSource(Stream src)
		{
			//todo: the try-catches in RecieveTryLoadSource functions
			//are there (for now) mainly to set the contentType
			//only if the loading+conversion succeeds, so it stays
			//at "unknown" otherwise. 
			//beware the lack of actual exception handling
			xml = XDocument.Load(src);
			//l.w("(MSXML)Xml stream parsed");

			contentType = eContentType.Xml;
		}
	}
}