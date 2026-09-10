using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using TMS_SharedLibrary.Models;
using Microsoft.Extensions.Logging;
using TMSTeacher.Models;
using TMSTeacher.Services;

namespace TMSTeacher.Controllers {
    [Authorize(Policy = "RequireTeacherRole")]
    public class ToyController : Controller
    {
        private readonly IStudentAPIService _studentAPIService;
        private readonly IToyAPIService _toyAPIService;
        private readonly ITeacherAPIService _teacherAPIService;
        private readonly IGeminiToySuggestionService _geminiToySuggestionService;
        private readonly IGraphAPIService _graphAPIService;
        private readonly ILogger<ToyController> _logger;
        private readonly IWebHostEnvironment _imgs;
        private readonly IConfiguration _config;

        public ToyController(IStudentAPIService studentAPIService, IToyAPIService toyAPIService, ITeacherAPIService teacherAPIService, IGeminiToySuggestionService geminiToySuggestionService, IGraphAPIService graphAPIService, ILogger<ToyController> logger, IWebHostEnvironment imgs, IConfiguration config) {
            _studentAPIService = studentAPIService;
            _toyAPIService = toyAPIService;
            _teacherAPIService = teacherAPIService;
            _geminiToySuggestionService = geminiToySuggestionService;
            _graphAPIService = graphAPIService;
            _logger = logger;
            _imgs = imgs;
            _config = config;
        }

        public async Task<IActionResult> Index(string? materials, string? categories, string searchQuery = null, bool redirectedFromNoResults = false) {
            try {
                ViewBag.SelectedMaterial = materials;
                ViewBag.SelectedCategory = categories;
                ViewBag.SearchQuery = searchQuery;

                string[]? materialArray = !string.IsNullOrEmpty(materials) ? new[] { materials } : null;
                string[]? categoryArray = !string.IsNullOrEmpty(categories) ? new[] { categories } : null;

                IEnumerable<Toy> toys = !string.IsNullOrEmpty(materials) || !string.IsNullOrEmpty(categories)
                    ? await _toyAPIService.GetFiltered(materialArray, categoryArray)
                    : await _toyAPIService.GetAll();

                toys = (toys ?? Enumerable.Empty<Toy>()).Where(t => t.IsActive);
                if (!string.IsNullOrEmpty(searchQuery))
                {
                    searchQuery = searchQuery.ToLower().Trim();
                    toys = toys.Where(t =>
                        (t.Name != null && t.Name.ToLower().Contains(searchQuery)) ||
                        (t.Description != null && t.Description.ToLower().Contains(searchQuery)) ||
                        (t.LocationCode != null && t.LocationCode.ToLower().Contains(searchQuery))
                    );
                }

                if (!string.IsNullOrEmpty(searchQuery) && !toys.Any())
                {
                    TempData["NoToysFoundMessage"] = "No toys found";

                    if (!redirectedFromNoResults)
                    {
                        return RedirectToAction(nameof(Index), new
                        {
                            materials,
                            categories,
                            searchQuery,
                            redirectedFromNoResults = true
                        });
                    }
                }

                // Create view models with student information for reserved toys
                var toyViewModels = new List<ToyWithStudentViewModel>();
                foreach (var toy in toys)
                {
                    var viewModel = new ToyWithStudentViewModel { Toy = toy };
                    
                    if (!toy.IsAvailable)
                    {
                        var (loan, student) = await _studentAPIService.GetCurrentLoanForToy(toy.ToyId);
                        viewModel.ReservedByStudent = student;
                        
                        // Load student name using Graph API
                        if (student != null)
                        {
                            await viewModel.LoadStudentNameAsync(_graphAPIService);
                        }
                    }
                    
                    toyViewModels.Add(viewModel);
                }

                var sortedToys = toyViewModels
                    .OrderByDescending(vm => vm.Toy.IsAvailable)
                    .ThenBy(vm => vm.Toy.Name);

                return View(sortedToys);
            } catch {
                TempData["Error"] = "An error occurred while loading toys. Please try again later.";
                return View(new List<ToyWithStudentViewModel>());
            }
        }

        // GET: Toy/Details/5
        public async Task<IActionResult> Details(int id) {

            if (id <= 0)
                return NotFound();

            try {
                var toy = await _toyAPIService.Get(id);

                if (toy == null)
                    return NotFound();

                return View(toy);
            } catch {
                return NotFound();
            }
        }

        // GET: Toy/Create
        public async Task<IActionResult> Create() {
            try {
                var teachers = await _teacherAPIService.GetAll();
                ViewData["ManagedBy"] = new SelectList(teachers, "TeacherId", "TeacherId");
            } catch (Exception ex) {
                _logger.LogError(ex, "Failed to load teachers for toy creation.");
                TempData["Error"] = "Unable to load teacher data right now. You can try again shortly.";
                ViewData["ManagedBy"] = new SelectList(new List<Teacher>(), "TeacherId", "TeacherId");
            }

            return View();
        }

        // POST: Toy/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("ToyId,Name,Description,AdditionalInformation,Category,Material,LocationCode,ImagePath,IsAvailable,ManagedBy")] Toy toy, IFormFile? imageFile) {

            if (ModelState.IsValid) {
                try {
                    if (imageFile != null && imageFile.Length > 0) {
                        var fileName = await _toyAPIService.UploadToyImageAsync(imageFile);

                        if (string.IsNullOrWhiteSpace(fileName)) {
                            ModelState.AddModelError(string.Empty, "Image upload failed.");
                            var teachersFail = await _teacherAPIService.GetAll();
                            ViewData["ManagedBy"] = new SelectList(teachersFail, "TeacherId", "FirstName", toy.ManagedBy);
                            return View(toy);
                        }

                        toy.ImagePath = fileName;
                    }

                    toy.IsActive = true;
                    var success = await _toyAPIService.Create(toy);

                    if (success) {
                        return RedirectToAction(nameof(Index));
                    }

                    ModelState.AddModelError(string.Empty, "Unable to save toy to API.");
                } catch (Exception) {
                    ModelState.AddModelError(string.Empty,
                        "An error occurred while creating the toy. Please try again later.");
                }
            }

            try {
                var teachers = await _teacherAPIService.GetAll();
                ViewData["ManagedBy"] = new SelectList(teachers, "TeacherId", "FirstName", toy.ManagedBy);
            } catch {
                ViewData["ManagedBy"] = new SelectList(new List<Teacher>(), "TeacherId", "FirstName");
            }
            return View(toy);
        }

        // GET: Toy/Edit/5
        public async Task<IActionResult> Edit(int id) {
            if (id <= 0) {
                return NotFound();
            }
            var toy = await _toyAPIService.Get(id);
            if (toy == null) {
                return NotFound();
            }

            try {
                var teachers = await _teacherAPIService.GetAll();
                ViewData["ManagedBy"] = new SelectList(teachers, "TeacherId", "TeacherId", toy.ManagedBy);
               
            } catch {
                ViewData["ManagedBy"] = new SelectList(new List<Teacher>(), "TeacherId", "TeacherId");
            }

            return View(toy);
        }


        // POST: Toy/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [Bind("ToyId,Name,Description,AdditionalInformation,Category,Material,LocationCode,ImagePath,IsAvailable,ManagedBy")] Toy toy, IFormFile? imageFile) {

            if (id != toy.ToyId) {
                return NotFound();
            }

            var existing = await _toyAPIService.Get(id);
            if (existing == null) {
                return NotFound();
            }

            if (ModelState.IsValid) {
                try {
                    if (imageFile != null && imageFile.Length > 0) {
                        var fileName = await _toyAPIService.UploadToyImageAsync(imageFile);

                        if (string.IsNullOrWhiteSpace(fileName)) {
                            ModelState.AddModelError(string.Empty, "Image upload failed.");
                            var teachersFail = await _teacherAPIService.GetAll();
                            ViewData["ManagedBy"] = new SelectList(teachersFail, "TeacherId", "TeacherId", toy.ManagedBy);
                            return View(toy);
                        }

                        toy.ImagePath = fileName;
                    } else {
                        toy.ImagePath = existing.ImagePath;
                    }

                    toy.IsActive = existing.IsActive;
                    var success = await _toyAPIService.UpdateToy(id, toy);

                    if (success) {
                        return RedirectToAction(nameof(Index));
                    }

                    ModelState.AddModelError(string.Empty, "Unable to update toy in API.");
                } catch (Exception) {
                    ModelState.AddModelError(string.Empty,
                        "An error occurred while updating the toy. Please try again later.");
                }
            }
            try {
                var teachers = await _teacherAPIService.GetAll();
                ViewData["ManagedBy"] = new SelectList(teachers, "TeacherId", "TeacherId", toy.ManagedBy);
            } catch {
                ViewData["ManagedBy"] = new SelectList(new List<Teacher>(), "TeacherId", "TeacherId");
            }

            return View(toy);
        }

        // GET: Toy/Delete/5
        public async Task<IActionResult> Delete(int id) {

            if (id == null) {
                return NotFound();
            }
            if(id <= 0) {
                return NotFound();
            }

            var toy = await _toyAPIService.Get(id);
            if (toy == null) {
                return NotFound();
            }

            return View(toy);
        }

        // POST: Toy/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(int id) {
            try {
                var success = await _toyAPIService.Delete(id);

                if (!success) {
                    TempData["ErrorMessage"] = "Cannot delete this toy because it is currently borrowed.";
                } else {
                    TempData["SuccessMessage"] = "Toy deleted successfully.";
                }
            } catch (Exception) {
                TempData["ErrorMessage"] = "An error occurred while deleting the toy.";
            }

            return RedirectToAction(nameof(Index));
        }

        // GET: Toy/Return/5
        public async Task<IActionResult> Return(int id) {
            if (id <= 0) {
                return NotFound();
            }

            try {
                var toy = await _toyAPIService.Get(id);
                if (toy == null) {
                    return NotFound();
                }

                // Only show return page if toy is currently reserved
                if (toy.IsAvailable) {
                    TempData["ErrorMessage"] = "This toy is not currently reserved.";
                    return RedirectToAction(nameof(Index));
                }

                return View("Unreserve", toy);
            } catch {
                TempData["ErrorMessage"] = "An error occurred while loading toy details.";
                return RedirectToAction(nameof(Index));
            }
        }

        // POST: Toy/ReturnConfirmed/5
        [HttpPost, ActionName("ReturnConfirmed")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReturnConfirmed(int id) {
            try {
                var toy = await _toyAPIService.Get(id);
                if (toy == null) {
                    return NotFound();
                }

                // Check if toy is actually reserved
                if (toy.IsAvailable) {
                    TempData["ErrorMessage"] = "This toy is not currently reserved.";
                    return RedirectToAction(nameof(Index));
                }

                // Use the proper ReturnToy API to set ReturnDate to today and make toy available
                await _studentAPIService.ReturnToy(id);

                TempData["SuccessMessage"] = "Toy returned successfully and is now available.";
            } catch (Exception ex) {
                _logger.LogError(ex, "Error occurred while returning toy with ID: {ToyId}", id);
                TempData["ErrorMessage"] = "An error occurred while returning the toy.";
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SuggestToyFields(string name, IFormFile? imageFile)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { error = "Toy name is required." });
            }

            if (imageFile == null || imageFile.Length == 0)
            {
                return BadRequest(new { error = "Toy image is required." });
            }

            try
            {
                var suggestion = await _geminiToySuggestionService.SuggestToyFieldsAsync(name, imageFile);

                if (suggestion == null)
                {
                    return StatusCode(502, new { error = "No suggestion was returned by Gemini." });
                }

                return Json(suggestion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate AI toy suggestions for '{ToyName}'.", name);
                return StatusCode(500, new { error = "Unable to generate AI suggestions right now." });
            }
        }
    }
}
