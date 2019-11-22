﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using AzureDevOpsDemoBuilder.Extensions;
using AzureDevOpsDemoBuilder.ExtractorModels;
using AzureDevOpsDemoBuilder.Models;
using AzureDevOpsDemoBuilder.ServiceInterfaces;
using AzureDevOpsDemoBuilder.Services;
using AzureDevOpsAPI.Extractor;
using AzureDevOpsAPI.ProjectsAndTeams;
using AzureDevOpsAPI.Viewmodel.ProjectAndTeams;
using AzureDevOpsAPI.WorkItemAndTracking;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using AzureDevOpsAPI;
using Microsoft.Extensions.Logging;

namespace AzureDevOpsDemoBuilder.Controllers
{

    public class ExtractorController : Controller
    {

        private delegate string[] ProcessEnvironment(Project model);
        private IExtractorService extractorService;

        public IConfiguration AppKeyConfiguration { get; }

        private ILogger<ExtractorController> logger;
        private IAccountService accountService;
        private IWebHostEnvironment HostingEnvironment;
        public ExtractorController(IConfiguration configuration, IAccountService _accountService,
            IExtractorService _extractorService, IWebHostEnvironment hostEnvironment, ILogger<ExtractorController> _logger)
        {
            HostingEnvironment = hostEnvironment;
            accountService = _accountService;
            extractorService = _extractorService;
            AppKeyConfiguration = configuration;
            logger = _logger;
        }

        [AllowAnonymous]
        public ActionResult PageNotFound()
        {
            return View();
        }

        // Get the current progress of work done
        [HttpGet]
        [AllowAnonymous]
        public ContentResult GetCurrentProgress(string id)
        {
            this.ControllerContext.HttpContext.Response.Headers.Add("cache-control", "no-cache");
            var currentProgress = ExtractorService.GetStatusMessage(id).ToString();
            return Content(currentProgress);
        }
        [AllowAnonymous]
        public ActionResult Index(ProjectList.ProjectDetails model)
        {
            try
            {
                AccessDetails accessDetails = new AccessDetails();
                string pat = "";
                string email = "";
                if (HttpContext.Session.GetString("PAT") != null)
                {
                    pat = HttpContext.Session.GetString("PAT");
                }
                if (HttpContext.Session.GetString("Email") != null)
                {
                    email = HttpContext.Session.GetString("PAT");
                }
                if (HttpContext.Session.GetString("EnableExtractor") == null || HttpContext.Session.GetString("EnableExtractor").ToLower() == "false")
                {
                    return RedirectToAction("NotFound");
                }
                if (string.IsNullOrEmpty(pat))
                {
                    return Redirect("../Account/Verify");
                }
                else
                {
                    accessDetails.access_token = pat;
                    ProfileDetails profile = accountService.GetProfile(accessDetails);
                    if (profile == null)
                    {
                        ViewBag.ErrorMessage = "Could not fetch your profile details, please try to login again";
                        return View(model);
                    }
                    if (profile.displayName != null && profile.emailAddress != null)
                    {
                        HttpContext.Session.SetString("User", profile.displayName);
                        HttpContext.Session.SetString("Email", profile.emailAddress.ToLower());
                    }
                    AccountsResponse.AccountList accountList = accountService.GetAccounts(profile.id, accessDetails);
                    model.accessToken = accessDetails.access_token;
                    model.accountsForDropdown = new List<string>();
                    if (accountList.count > 0)
                    {
                        foreach (var account in accountList.value)
                        {
                            model.accountsForDropdown.Add(account.accountName);
                        }
                        model.accountsForDropdown.Sort();
                    }
                    else
                    {
                        model.accountsForDropdown.Add("Select Organization");
                        ViewBag.AccDDError = "Could not load your organizations. Please change the directory in profile page of Azure DevOps Organization and try again.";
                    }
                    return View(model);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex.Message + "\n" + ex.StackTrace + "\n");
            }
            return View(model);
        }

        // Get Project List from the selected Organization
        [AllowAnonymous]
        public JsonResult GetprojectList(string accname, string pat)
        {
            string defaultHost = AppKeyConfiguration["DefaultHost"];
            string ProjectCreationVersion = AppKeyConfiguration["ProjectCreationVersion"];

            AppConfiguration config = new AppConfiguration() { AccountName = accname, PersonalAccessToken = pat, UriString = defaultHost + accname, VersionNumber = ProjectCreationVersion };
            Projects projects = new Projects(config);
            HttpResponseMessage response = projects.GetListOfProjects();
            ProjectsResponse.ProjectResult projectResult = new ProjectsResponse.ProjectResult();
            if (response.IsSuccessStatusCode)
            {
                // set the viewmodel from the content in the response
                projectResult = response.Content.ReadAsAsync<ProjectsResponse.ProjectResult>().Result;
                projectResult.value = projectResult.value.OrderBy(x => x.name).ToList();
            }
            try
            {
                if (string.IsNullOrEmpty(projectResult.errmsg))
                {
                    if (projectResult.count == 0)
                    {
                        projectResult.errmsg = "No projects found!";
                    }
                    return Json(projectResult);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex.Message + "\n" + ex.StackTrace + "\n");
                projectResult.errmsg = ex.Message.ToString();
                string message = ex.Message.ToString();
            }
            return Json(projectResult);
        }

        //Get Project Properties to knwo which process template it is following
        [AllowAnonymous]
        public JsonResult GetProjectProperties(string accname, string project, string _credentials)
        {
            try
            {
                string defaultHost = AppKeyConfiguration["DefaultHost"];
                string ProjectPropertyVersion = AppKeyConfiguration["ProjectPropertyVersion"];

                AppConfiguration config = new AppConfiguration() { AccountName = accname, PersonalAccessToken = _credentials, UriString = defaultHost + accname, VersionNumber = ProjectPropertyVersion, ProjectId = project };

                ProjectProperties.Properties load = new ProjectProperties.Properties();
                Projects projects = new Projects(config);
                load = projects.GetProjectProperties();
                if (load.count > 0)
                {
                    if (load.TypeClass != null)
                    {
                        return Json(load);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex.Message + "\n" + ex.StackTrace + "\n");
            }
            return new JsonResult(string.Empty);

        }

        // End the extraction process
        public void EndEnvironmentSetupProcess(IAsyncResult result)
        {
            ProcessEnvironment processTask = (ProcessEnvironment)result.AsyncState;
            string[] strResult = processTask.EndInvoke(result);

            ExtractorService.RemoveKey(strResult[0]);
            if (ExtractorService.StatusMessages.Keys.Count(x => x == strResult[0] + "_Errors") == 1)
            {
                string errorMessages = ExtractorService.statusMessages[strResult[0] + "_Errors"];
                if (errorMessages != "")
                {
                    //also, log message to file system
                    string logPath = HostingEnvironment.WebRootPath + @"\Log";
                    string accountName = strResult[1];
                    string fileName = string.Format("{0}_{1}.txt", "Extractor_", DateTime.Now.ToString("ddMMMyyyy_HHmmss"));

                    if (!Directory.Exists(logPath))
                    {
                        Directory.CreateDirectory(logPath);
                    }
                    System.IO.File.AppendAllText(Path.Combine(logPath, fileName), errorMessages);

                    //Create ISSUE work item with error details in VSTSProjectgenarator account
                    string patBase64 = AppKeyConfiguration["PATBase64"];
                    string url = AppKeyConfiguration["URL"];
                    string projectId = AppKeyConfiguration["PROJECTID"];
                    string issueName = string.Format("{0}_{1}", "Extractor_", DateTime.Now.ToString("ddMMMyyyy_HHmmss"));
                    IssueWI objIssue = new IssueWI();
                    logger.LogDebug("\t Extractor_" + errorMessages + "\n");
                    string logWIT = "true"; //AppKeyConfiguration["LogWIT"];
                    if (logWIT == "true")
                    {
                        objIssue.CreateIssueWI(patBase64, "4.1", url, issueName, errorMessages, projectId, "Extractor");
                    }
                }
            }
        }

        //Analyze the selected project to know what all the artifacts it has
        public ProjectConfigurations ProjectConfiguration(Project model)
        {
            string repoVersion = AppKeyConfiguration["RepoVersion"];
            string buildVersion = AppKeyConfiguration["BuildVersion"];
            string releaseVersion = AppKeyConfiguration["ReleaseVersion"];
            string wikiVersion = AppKeyConfiguration["WikiVersion"];
            string boardVersion = AppKeyConfiguration["BoardVersion"];
            string workItemsVersion = AppKeyConfiguration["WorkItemsVersion"];
            string releaseHost = AppKeyConfiguration["ReleaseHost"];
            string defaultHost = AppKeyConfiguration["DefaultHost"];
            string extensionHost = AppKeyConfiguration["ExtensionHost"];
            string getReleaseVersion = AppKeyConfiguration["GetRelease"];
            string agentQueueVersion = AppKeyConfiguration["AgentQueueVersion"];
            string extensionVersion = AppKeyConfiguration["ExtensionVersion"];
            string endpointVersion = AppKeyConfiguration["EndPointVersion"];
            string queriesVersion = AppKeyConfiguration["QueriesVersion"];
            ProjectConfigurations projectConfig = new ProjectConfigurations();

            projectConfig.AgentQueueConfig = new AppConfiguration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = wikiVersion };
            projectConfig.WorkItemConfig = new AppConfiguration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = wikiVersion };
            projectConfig.BuildDefinitionConfig = new AppConfiguration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = buildVersion };
            projectConfig.ReleaseDefinitionConfig = new AppConfiguration() { UriString = releaseHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = releaseVersion };
            projectConfig.RepoConfig = new AppConfiguration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = repoVersion };
            projectConfig.BoardConfig = new AppConfiguration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = boardVersion };
            projectConfig.Config = new AppConfiguration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id };
            projectConfig.GetReleaseConfig = new AppConfiguration() { UriString = releaseHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = getReleaseVersion };
            projectConfig.ExtensionConfig = new AppConfiguration() { UriString = extensionHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = extensionVersion };
            projectConfig.EndpointConfig = new AppConfiguration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = endpointVersion };
            projectConfig.QueriesConfig = new AppConfiguration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = queriesVersion };

            return projectConfig;
        }

        #region GetCounts
        // Start Analysis process
        [AllowAnonymous]
        public JsonResult AnalyzeProject(Project model)
        {
            ExtractorAnalysis analysis = new ExtractorAnalysis();
            ProjectConfigurations appConfig = extractorService.ProjectConfiguration(model);
            analysis.teamCount = extractorService.GetTeamsCount(appConfig);
            analysis.IterationCount = extractorService.GetIterationsCount(appConfig);
            analysis.WorkItemCounts = extractorService.GetWorkItemsCount(appConfig);
            analysis.BuildDefCount = extractorService.GetBuildDefinitionCount(appConfig);
            analysis.ReleaseDefCount = extractorService.GetReleaseDefinitionCount(appConfig);

            analysis.ErrorMessages = ExtractorService.errorMessages;
            return Json(analysis);
        }
        #endregion

        #region Extract Template
        //Initiate the extraction process
        [HttpPost]
        [AllowAnonymous]
        public bool StartEnvironmentSetupProcess(Project model)
        {
            HttpContext.Session.SetString("Project", model.ProjectName);
            ExtractorService.AddMessage(model.id, string.Empty);
            ExtractorService.AddMessage(model.id.ErrorId(), string.Empty);
            ProcessEnvironment processTask = new ProcessEnvironment(extractorService.GenerateTemplateArifacts);
            processTask.BeginInvoke(model, new AsyncCallback(EndEnvironmentSetupProcess), processTask);
            return true;
        }
        #endregion end extract template
        // Remove the template folder after zipping it
        [AllowAnonymous]
        private void RemoveFolder()
        {
            string projectName = HttpContext.Session.GetString("Project");
            if (projectName != "")
            {
                System.IO.File.Delete(ExtractorService.extractedTemplatePath + projectName);
                System.IO.File.Delete(ExtractorService.extractedTemplatePath + projectName + ".zip");
            }

        }

        [AllowAnonymous]
        public ActionResult ZipAndDownloadFiles(string fileName)
        {
            string filePath = HostingEnvironment.WebRootPath + @"ExtractedTemplate\" + fileName;
            try
            {
                CreateZips.SourceDirectoriesFiles sfiles = new CreateZips.SourceDirectoriesFiles();
                if (System.IO.Directory.Exists(filePath))
                {
                    string[] files = Directory.GetFiles(filePath);
                    string[] subDirs = Directory.GetDirectories(filePath);
                    if (files.Length > 0)
                    {
                        sfiles.Files = new List<CreateZips.FileInfo>();

                        foreach (var f in files)
                        {
                            CreateZips.FileInfo fileInfo = new CreateZips.FileInfo();

                            string[] fSplit = f.Split('\\');
                            string splitLength = fSplit[fSplit.Length - 1];
                            fSplit = splitLength.Split('.');

                            fileInfo.Name = fSplit[0];
                            fileInfo.Extension = fSplit[1];
                            fileInfo.FileBytes = System.IO.File.ReadAllBytes(f);
                            sfiles.Files.Add(fileInfo);
                        }
                    }

                    if (subDirs.Length > 0)
                    {
                        sfiles.Folder = new List<CreateZips.Folder>();

                        foreach (var dir in subDirs)
                        {
                            string[] subDirFiles = System.IO.Directory.GetFiles(dir);
                            string[] subDirsLevel2 = Directory.GetDirectories(dir);

                            if (subDirFiles.Length > 0)
                            {
                                CreateZips.Folder folder = new CreateZips.Folder();
                                string[] getFolderName = dir.Split('\\');
                                string subFolderName = getFolderName[getFolderName.Length - 1];
                                folder.FolderName = subFolderName;
                                folder.FolderItems = new List<CreateZips.FolderItem>();

                                foreach (var sdf in subDirFiles)
                                {
                                    CreateZips.FolderItem folderItem = new CreateZips.FolderItem();
                                    string[] fSplit = sdf.Split('\\');
                                    string splitLength = fSplit[fSplit.Length - 1];
                                    fSplit = splitLength.Split('.');

                                    folderItem.Name = fSplit[0];
                                    folderItem.Extension = fSplit[1];
                                    folderItem.FileBytes = System.IO.File.ReadAllBytes(sdf);
                                    folder.FolderItems.Add(folderItem);
                                }
                                if (subDirsLevel2.Length > 0)
                                {
                                    folder.FolderL2 = new List<CreateZips.FolderL2>();
                                    foreach (var dirL2 in subDirsLevel2)
                                    {
                                        string[] subDirFilesL2 = System.IO.Directory.GetFiles(dirL2);
                                        if (subDirFilesL2.Length > 0)
                                        {
                                            CreateZips.FolderL2 folderFL2 = new CreateZips.FolderL2();
                                            string[] getFolderNameL2 = dirL2.Split('\\');
                                            string subFolderNameL2 = getFolderNameL2[getFolderNameL2.Length - 1];
                                            folderFL2.FolderName = subFolderNameL2;
                                            folderFL2.FolderItems = new List<CreateZips.FolderItem>();

                                            foreach (var sdfL2 in subDirFilesL2)
                                            {
                                                CreateZips.FolderItem folderItem = new CreateZips.FolderItem();
                                                string[] fSplit = sdfL2.Split('\\');
                                                string splitLength = fSplit[fSplit.Length - 1];
                                                fSplit = splitLength.Split('.');

                                                folderItem.Name = fSplit[0];
                                                folderItem.Extension = fSplit[1];
                                                folderItem.FileBytes = System.IO.File.ReadAllBytes(sdfL2);
                                                folderFL2.FolderItems.Add(folderItem);
                                            }
                                            folder.FolderL2.Add(folderFL2);
                                        }
                                    }
                                }
                                sfiles.Folder.Add(folder);
                            }
                        }
                    }
                }
                // ...

                // the output bytes of the zip
                byte[] fileBytes = null;

                //create a working memory stream
                using (System.IO.MemoryStream memoryStream = new System.IO.MemoryStream())
                {
                    // create a zip
                    using (System.IO.Compression.ZipArchive zip = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create, true))
                    {
                        // interate through the source files
                        if (sfiles.Folder != null && sfiles.Folder.Count > 0)
                        {
                            //each folder in source file [depth 1]
                            foreach (var folder in sfiles.Folder)
                            {
                                // add the item name to the zip
                                // each file in the folder
                                foreach (var file in folder.FolderItems)
                                {
                                    // folder items - file name, extension, and file bytes or content in bytes
                                    // zip.CreateEntry can create folder or the file. If you just provide a name, it will create a folder (if it doesn't not exist). If you provide with extension, it will create file 
                                    System.IO.Compression.ZipArchiveEntry zipItem = zip.CreateEntry(folder.FolderName + "/" + file.Name + "." + file.Extension); // Creating folder and create file inside that folder

                                    using (System.IO.MemoryStream originalFileMemoryStream = new System.IO.MemoryStream(file.FileBytes)) // adding file bytes to memory stream object
                                    {
                                        using (System.IO.Stream entryStream = zipItem.Open()) // opening the folder/file
                                        {
                                            originalFileMemoryStream.CopyTo(entryStream); // copy memory stream dat bytes to file created
                                        }
                                    }
                                    // for second level of folder like /Template/Teams/BoardColums.json
                                    //each folder in source file [depth 2]
                                    if (folder.FolderL2 != null && folder.FolderL2.Count > 0)
                                    {
                                        foreach (var folder2 in folder.FolderL2)
                                        {
                                            foreach (var file2 in folder2.FolderItems)
                                            {
                                                System.IO.Compression.ZipArchiveEntry zipItem2 = zip.CreateEntry(folder.FolderName + "/" + folder2.FolderName + "/" + file2.Name + "." + file2.Extension);
                                                using (System.IO.MemoryStream originalFileMemoryStreamL2 = new System.IO.MemoryStream(file2.FileBytes))
                                                {
                                                    using (System.IO.Stream entryStreamL2 = zipItem2.Open())
                                                    {
                                                        originalFileMemoryStreamL2.CopyTo(entryStreamL2);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        if (sfiles.Files != null && sfiles.Files.Count > 0)
                        {
                            foreach (var outerFile in sfiles.Files)
                            {
                                // add the item name to the zip
                                System.IO.Compression.ZipArchiveEntry zipItem = zip.CreateEntry(outerFile.Name + "." + outerFile.Extension);
                                // add the item bytes to the zip entry by opening the original file and copying the bytes 
                                using (System.IO.MemoryStream originalFileMemoryStream = new System.IO.MemoryStream(outerFile.FileBytes))
                                {
                                    using (System.IO.Stream entryStream = zipItem.Open())
                                    {
                                        originalFileMemoryStream.CopyTo(entryStream);
                                    }
                                }
                            }
                        }
                    }
                    fileBytes = memoryStream.ToArray();
                }
                // download the constructed zip
                Directory.Delete(filePath, true);
                HttpContext.Response.Headers.Add("Content-Disposition", "attachment; filename=" + fileName + ".zip");
                return File(fileBytes, "application/zip");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex.Message + "\n" + ex.StackTrace + "\n");
            }
            finally
            {
                if (Directory.Exists(filePath))
                {
                    Directory.Delete(filePath, true);
                }
            }
            ViewBag.Error = "File not found";
            return RedirectToAction("Index", "Extractor");
        }
    }
}