// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Mike Krüger" email="mike@icsharpcode.net"/>
//     <version value="$version"/>
// </file>

using System;
using System.Xml;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.CodeDom.Compiler;

using MonoDevelop.Projects;
using MonoDevelop.Core;
using Mono.Addins;
using MonoDevelop.Core.Gui;
using MonoDevelop.Core.Gui.Codons;
using MonoDevelop.Ide.Codons;

namespace MonoDevelop.Ide.Gui
{
	/// <summary>
	/// This class handles the installed display bindings
	/// and provides a simple access point to these bindings.
	/// </summary>
	public class DisplayBindingService : AbstractService
	{
		readonly static string displayBindingPath = "/SharpDevelop/Workbench/DisplayBindings";
		List<DisplayBindingCodon> bindings = null;

		public IDisplayBinding LastBinding {
			get {
				return bindings[0].DisplayBinding;
			}
		}
		
		public IDisplayBinding GetBindingPerFileName(string filename)
		{
			DisplayBindingCodon codon = GetCodonPerFileName(filename);
			return codon == null ? null : codon.DisplayBinding;
		}
		
		public IDisplayBinding GetBindingForMimeType (string mimeType)
		{
			foreach (DisplayBindingCodon binding in bindings) {
				if (binding.DisplayBinding != null && binding.DisplayBinding.CanCreateContentForMimeType (mimeType)) {
					return binding.DisplayBinding;
				}
			}
			return null;
		}
		
		public IDisplayBinding[] GetBindingsForMimeType (string mimeType)
		{
			ArrayList list = new ArrayList ();
			foreach (DisplayBindingCodon binding in bindings) {
				if (binding.DisplayBinding != null && binding.DisplayBinding.CanCreateContentForMimeType (mimeType)) {
					list.Add (binding.DisplayBinding);
				}
			}
			return (IDisplayBinding[]) list.ToArray (typeof(IDisplayBinding));
		}
		
		internal DisplayBindingCodon GetCodonPerFileName(string filename)
		{
			string vfsname = filename;
			vfsname = vfsname.Replace ("%", "%25");
			vfsname = vfsname.Replace ("#", "%23");
			vfsname = vfsname.Replace ("?", "%3F");
			string mimetype = Gnome.Vfs.MimeType.GetMimeTypeForUri (vfsname);

			foreach (DisplayBindingCodon binding in bindings) {
				if (binding.DisplayBinding != null && binding.DisplayBinding.CanCreateContentForFile(filename)) {
					return binding;
				}
			}
			if (!filename.StartsWith ("http")) {
				foreach (DisplayBindingCodon binding in bindings) {
					if (binding.DisplayBinding != null && binding.DisplayBinding.CanCreateContentForMimeType (mimetype)) {
						return binding;
					}
				}
			}
			return null;
		}
		
		internal void AttachSubWindows(IWorkbenchWindow workbenchWindow)
		{
			foreach (DisplayBindingCodon binding in bindings) {
				if (binding.SecondaryDisplayBinding != null && binding.SecondaryDisplayBinding.CanAttachTo(workbenchWindow.ViewContent)) {
					workbenchWindow.AttachSecondaryViewContent(binding.SecondaryDisplayBinding.CreateSecondaryViewContent(workbenchWindow.ViewContent));
				}
			}
		}
		
		public override void InitializeService ()
		{
			bindings = new List<DisplayBindingCodon> ();
			AddinManager.AddExtensionNodeHandler (displayBindingPath, OnExtensionChanged);
		}
		
		void OnExtensionChanged (object s, ExtensionNodeEventArgs args)
		{
			if (args.Change == ExtensionChange.Add)
				bindings.Add ((DisplayBindingCodon)args.ExtensionNode);
			else
				bindings.Remove ((DisplayBindingCodon)args.ExtensionNode);
		}
	}
}
