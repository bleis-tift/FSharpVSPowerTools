﻿using System.Diagnostics;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Shell;
using FSharpVSPowerTools.ProjectSystem;
using FSharpVSPowerTools.Navigation;

namespace FSharpVSPowerTools
{
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("F#")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal class GoToDefinitionFilterProvider : IVsTextViewCreationListener
    {
        [Import]
        private IVsEditorAdaptersFactoryService editorFactory = null;

        [Import]
        private VSLanguageService fsharpVsLanguageService = null;

        [Import(typeof(SVsServiceProvider))]
        private System.IServiceProvider serviceProvider = null;

        [Import(typeof(ProjectFactory))]
        private ProjectFactory projectFactory = null;

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            var textView = editorFactory.GetWpfTextView(textViewAdapter);
            if (textView == null) return;

            var generalOptions = serviceProvider.GetService(typeof(GeneralOptionsPage)) as GeneralOptionsPage;
            //if (!generalOptions.FindAllReferencesEnabled) return;

            AddCommandFilter(textViewAdapter, new GoToDefinitionFilter(textView, fsharpVsLanguageService, serviceProvider,
                                                                       projectFactory));
        }

        private static void AddCommandFilter(IVsTextView viewAdapter, GoToDefinitionFilter commandFilter)
        {
            if (!commandFilter.IsAdded)
            {
                // Get the view adapter from the editor factory
                IOleCommandTarget next;
                int hr = viewAdapter.AddCommandFilter(commandFilter, out next);

                if (hr == VSConstants.S_OK)
                {
                    commandFilter.IsAdded = true;
                    // You'll need the next target for Exec and QueryStatus
                    if (next != null) commandFilter.NextTarget = next;
                }
            }
        }

    }
}