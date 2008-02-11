// ExtendibleTextEditor.cs
//
// Author:
//   Mike Krüger <mkrueger@novell.com>
//
// Copyright (c) 2008 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Gtk;

using Mono.TextEditor;
using MonoDevelop.Ide.Gui;
using MonoDevelop.Ide.Gui.Content;
using MonoDevelop.Projects.Parser;
using MonoDevelop.Projects.Gui.Completion;
using MonoDevelop.Components.Commands;
using Mono.TextEditor.Highlighting;
using MonoDevelop.Ide.CodeTemplates;

namespace MonoDevelop.SourceEditor
{
	public class ExtendibleTextEditor : Mono.TextEditor.TextEditor
	{
		ITextEditorExtension extension = null;
		LanguageItemWindow languageItemWindow;
		SourceEditorView view;
		
		const int LanguageItemTipTimer = 800;
		ILanguageItem tipItem;
		bool showTipScheduled;
		int langTipX, langTipY;
		uint tipTimeoutId;
		
		public ITextEditorExtension Extension {
			get {
				return extension;
			}
			set {
				extension = value;
			}
		}
		
		public ExtendibleTextEditor (SourceEditorView view, Mono.TextEditor.Document doc) : base (doc)
		{
			Initialize (view);
		}
		
		public ExtendibleTextEditor (SourceEditorView view)
		{
			Initialize (view);
		}
		
		void Initialize (SourceEditorView view)
		{
			this.view = view;
			this.Buffer.TextReplaced += delegate {
				this.HideLanguageItemWindow ();
			};
			base.TextEditorData.Caret.PositionChanged += delegate {
				if (extension != null)
					extension.CursorPositionChanged ();
			};
			base.TextEditorData.Document.Buffer.TextReplaced += delegate (object sender, ReplaceEventArgs args) {
				if (extension != null)
					extension.TextChanged (args.Offset, args.Offset + Math.Max (args.Count, args.Value != null ? args.Value.Length : 0));
			};
			keyBindings [GetKeyCode (Gdk.Key.Tab)] = new TabAction (this);
			keyBindings [GetKeyCode (Gdk.Key.BackSpace)] = new AdvancedBackspaceAction ();
			this.PopupMenu += delegate {
				this.ShowPopup ();
			};
			this.ButtonPressEvent += delegate(object sender, Gtk.ButtonPressEventArgs args) {
				if (args.Event.Button == 3) {
					this.ShowPopup ();
				}
			};
			this.Realized += delegate {
				FireOptionsChange ();
			};
		}
		public void FireOptionsChange ()
		{
			this.OptionsChanged (null, null);
		}
		
		protected override bool OnKeyPressEvent (Gdk.EventKey evnt)
		{
			bool result = true;
			char ch = (char)evnt.Key;
			if (extension != null) {
				if (!extension.KeyPress (evnt.Key, evnt.State)) {
					result = base.OnKeyPressEvent (evnt);
				}
			} else {
				result = base.OnKeyPressEvent (evnt);				
			}
			if (SourceEditorOptions.Options.AutoInsertTemplates && IsTemplateKnown ())
				DoInsertTemplate ();
			if (SourceEditorOptions.Options.AutoInsertMatchingBracket) {
				switch (ch) {
				case '{':
					if (extension != null) {
						int offset = Caret.Offset;
						extension.KeyPress (Gdk.Key.Return, Gdk.ModifierType.None);
						extension.KeyPress ((Gdk.Key)'}', Gdk.ModifierType.None);
						Caret.Offset = offset;
						extension.KeyPress (Gdk.Key.Return, Gdk.ModifierType.None);
					} else {
						base.SimulateKeyPress (Gdk.Key.Return, Gdk.ModifierType.None);
						Buffer.Insert (Caret.Offset, new StringBuilder ("}"));
					}
					break;
				case '[':
					Buffer.Insert (Caret.Offset, new StringBuilder ("]"));
					break;
				case '(':
					Buffer.Insert (Caret.Offset, new StringBuilder (")"));
					break;
				case '<':
					Buffer.Insert (Caret.Offset, new StringBuilder (">"));
					break;
				case '\'':
					Buffer.Insert (Caret.Offset, new StringBuilder ("'"));
					break;
				case '"':
					Buffer.Insert (Caret.Offset, new StringBuilder ("\""));
					break;
				}
			}
			return result;
		}
		public TextViewMargin TextViewMargin {
			get {
				return textViewMargin;
			}
		}
		
		double mx, my;
		protected override bool OnMotionNotifyEvent (Gdk.EventMotion evnt)
		{
			mx = evnt.X - textViewMargin.XOffset;
			my = evnt.Y;
			bool result = base.OnMotionNotifyEvent (evnt);
			UpdateLanguageItemWindow ();
			return result;
		}
		
		void UpdateLanguageItemWindow ()
		{
			if (languageItemWindow != null) {
				// Tip already being shown. Update it.
				ShowTooltip ();
			}
			else if (showTipScheduled) {
				// Tip already scheduled. Reset the timer.
				GLib.Source.Remove (tipTimeoutId);
				tipTimeoutId = GLib.Timeout.Add (LanguageItemTipTimer, ShowTooltip);
			}
			else {
				// Start a timer to show the tip
				showTipScheduled = true;
				tipTimeoutId = GLib.Timeout.Add (LanguageItemTipTimer, ShowTooltip);
			}
		}
		
		bool ShowTooltip ()
		{
			string errorInfo;

			showTipScheduled = false;
			int xloc = (int)mx;
			int yloc = (int)my;
			ILanguageItem item = GetLanguageItem (Document.LocationToOffset (base.VisualToDocumentLocation ((int)mx, (int)my)));
			if (item != null) {
				// Tip already being shown for this language item?
				if (languageItemWindow != null && tipItem != null && tipItem.Equals (item))
					return false;
				
				langTipX = xloc;
				langTipY = yloc;
				tipItem = item;

				HideLanguageItemWindow ();
				
				IParserContext pctx = view.GetParserContext ();
				if (pctx == null)
					return false;

				DoShowTooltip (new LanguageItemWindow (tipItem, pctx, view.GetAmbience (), 
				                                        GetErrorInformationAt (Caret.Offset)), langTipX, langTipY);
				
				
			} else if (!string.IsNullOrEmpty ((errorInfo = GetErrorInformationAt(Caret.Offset)))) {
				// Error tooltip already shown
				if (languageItemWindow != null /*&& tiItem == ti.Line*/)
					return false;
				//tiItem = ti.Line;
				
				HideLanguageItemWindow ();
				DoShowTooltip (new LanguageItemWindow (null, null, null, errorInfo), xloc, yloc);
			} else
				HideLanguageItemWindow ();
			
			return false;
		}
		
		void DoShowTooltip (LanguageItemWindow liw, int xloc, int yloc)
		{
			languageItemWindow = liw;
			
			int ox = 0, oy = 0;
			
			this.GdkWindow.GetOrigin (out ox, out oy);
			int w = languageItemWindow.Child.SizeRequest ().Width;
			languageItemWindow.Move (xloc + ox - (w/2), yloc + oy + 20);
			languageItemWindow.ShowAll ();
		}
		
		protected override void OnUnrealized ()
		{
			if (showTipScheduled) {
				GLib.Source.Remove (tipTimeoutId);
				showTipScheduled = false;
			}
			base.OnUnrealized ();
		}
		string GetErrorInformationAt (int offset)
		{
//			ErrorInfo info;
//			if (errors.TryGetValue (iter.Line, out info))
//				return "<b>" + GettextCatalog.GetString ("Parser Error:") + "</b> " + info.Message;
//			else
				return null;
		}
		
		ILanguageItem GetLanguageItem (int offset)
		{
			string txt = this.Document.Buffer.Text;
			string fileName = view.ContentName;
			if (fileName == null)
				fileName = view.UntitledName;

			IParserContext ctx = view.GetParserContext ();
			if (ctx == null)
				return null;

			IExpressionFinder expressionFinder = null;
			if (fileName != null)
				expressionFinder = ctx.GetExpressionFinder (fileName);

			string expression = expressionFinder == null ? TextUtilities.GetExpressionBeforeOffset (view, offset) : expressionFinder.FindFullExpression (txt, offset).Expression;
			if (expression == null)
				return null;
			
			int lineNumber = this.Document.Splitter.OffsetToLineNumber (offset);
			LineSegment line = this.Document.GetLine (lineNumber);

			return ctx.ResolveIdentifier (expression, lineNumber + 1, line.Offset + 1, fileName, txt);
		}		
		

		protected override bool OnLeaveNotifyEvent (Gdk.EventCrossing evnt)		
		{
			HideLanguageItemWindow ();
			return base.OnLeaveNotifyEvent (evnt);
		}
		
		protected override bool OnScrollEvent (Gdk.EventScroll evnt)
		{
			HideLanguageItemWindow ();
			return base.OnScrollEvent (evnt);
		}
		
		public void HideLanguageItemWindow ()
		{
			if (showTipScheduled) {
				GLib.Source.Remove (tipTimeoutId);
				showTipScheduled = false;
			}
			if (languageItemWindow != null) {
				languageItemWindow.Destroy ();
				languageItemWindow = null;
			}
		}

		void ShowPopup ()
		{
			HideLanguageItemWindow ();
			CommandEntrySet cset = IdeApp.CommandService.CreateCommandEntrySet ("/MonoDevelop/SourceEditor2/ContextMenu/Editor");
			Gtk.Menu menu = IdeApp.CommandService.CreateMenu (cset);
			menu.Destroyed += delegate {
				this.QueueDraw ();
			};
			IdeApp.CommandService.ShowContextMenu (menu);
		}
		
		
//		protected override void OnPopulatePopup (Menu menu)
//		{
//			
//			CommandEntrySet cset = IdeApp.CommandService.CreateCommandEntrySet ("");
//			if (cset.Count > 0) {
//				cset.AddItem (Command.Separator);
//				IdeApp.CommandService.InsertOptions (menu, cset, 0);
//			}
//			base.OnPopulatePopup (menu);
//		}
//		
		
#region Templates
		int FindPrevWordStart (int offset)
		{
			while (--offset >= 0 && !Char.IsWhiteSpace (Buffer.GetCharAt (offset))) 
				;
			return ++offset;
		}

		public string GetWordBeforeCaret ()
		{
			int offset = this.Caret.Offset;
			int start  = FindPrevWordStart (offset);
			return Buffer.GetTextAt (start, offset - start);
		}
		
		public int DeleteWordBeforeCaret ()
		{
			int offset = this.Caret.Offset;
			int start  = FindPrevWordStart (offset);
			Buffer.Remove (start, offset - start);
			return start;
		}

		public string GetLeadingWhiteSpace (int lineNr)
		{
			LineSegment line = Document.GetLine (lineNr);
			int index = 0;
			while (index < line.EditableLength && Char.IsWhiteSpace (Buffer.GetCharAt (line.Offset + index)))
				index++;
 	   		return index > 0 ? Buffer.GetTextAt (line.Offset, index) : "";
		}

		public bool IsTemplateKnown ()
		{
			string word = GetWordBeforeCaret ();
			CodeTemplateGroup templateGroup = CodeTemplateService.GetTemplateGroupPerFilename (this.view.ContentName);
			if (String.IsNullOrEmpty (word) || templateGroup == null) 
				return false;
			
			bool result = false;
			foreach (CodeTemplate template in templateGroup.Templates) {
				if (template.Shortcut == word) {
					result = true;
				} else if (template.Shortcut.StartsWith (word)) {
					result = false;
					break;
				}
			}
			return result;
		}
		
		public bool DoInsertTemplate ()
		{
			string word = GetWordBeforeCaret ();
			CodeTemplateGroup templateGroup = CodeTemplateService.GetTemplateGroupPerFilename (this.view.ContentName);
			if (String.IsNullOrEmpty (word) || templateGroup == null) 
				return false;
			
			foreach (CodeTemplate template in templateGroup.Templates) {
				if (template.Shortcut == word) {
					InsertTemplate (template);
					return true;
				}
			}
			return false;
		}
		
		public void InsertTemplate (CodeTemplate template)
		{
			int offset = Caret.Offset;
			string word = GetWordBeforeCaret ().Trim ();
			if (word.Length > 0)
				offset = DeleteWordBeforeCaret ();
			
			string leadingWhiteSpace = GetLeadingWhiteSpace (Caret.Line);

			int finalCaretOffset = offset + template.Text.Length;
			StringBuilder builder = new StringBuilder ();
			for (int i = 0; i < template.Text.Length; ++i) {
				switch (template.Text[i]) {
				case '|':
					finalCaretOffset = i + offset;
					break;
				case '\r':
					break;
				case '\n':
					builder.Append (Environment.NewLine);
					builder.Append (leadingWhiteSpace);
					break;
				default:
					builder.Append (template.Text[i]);
					break;
				}
			}
			
//			if (endLine > beginLine) {
//				IndentLines (beginLine+1, endLine, leadingWhiteSpace);
//			}
			Buffer.Insert (offset, builder);
			Caret.Offset = finalCaretOffset;
		}		
#endregion
	}
}
